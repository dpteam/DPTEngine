/*
 * DPT.Engine Editor -- all editor logic.
 * Copyright (C) 2026 DartPower Team. GPLv2.
 *
 * ES5 only, deliberately. The C# host embeds this page in an MSHTML
 * WebBrowser control: IE11 on a modern Windows, IE8 on XP. Neither
 * understands let/const, arrow functions, template literals, classes or
 * promises, and the older one also lacks addEventListener, textContent,
 * dataset, JSON-less parsing and CSS custom properties. Everything below
 * is var + function + string concatenation, and every modern DOM call has
 * a fallback or a guard.
 *
 * The file is spliced into editor.html at run time by the host, so it
 * must never contain an end-of-script tag inside a string or comment.
 *
 * Layout of this file:
 *   branding -> constants -> utilities -> model -> serialisation
 *   -> host bridge -> tree -> timeline -> inspector -> preview
 *   -> events -> boot
 */
(function () {
    'use strict';

    // ================================================================ branding

    var BRAND = window.DPT_BRANDING || {};
    var BRAND_NAME = BRAND.name || 'DPT.Engine';
    var BRAND_VERSION = BRAND.version || '1.0.0';
    var BRAND_TAGLINE = BRAND.tagline || 'A Win32 + Direct3D 9 demo engine with a web editor';

    // The host may splice a ready-made title ("DPT.Engine v1.0.0"); the
    // fallback mirrors Branding.Title so a bare Assets folder still titles
    // the window correctly.
    var BRAND_TITLE = BRAND.title || (BRAND_NAME + ' v' + BRAND_VERSION);

    var THEMES = ['light', 'dark', 'oled'];

    // The runtime authors every effect rectangle in this space and scales
    // it to the render size. The editor previews in the same space so a
    // hand-written rect lands where the runtime would put it.
    var VIRTUAL_W = 800;
    var VIRTUAL_H = 600;

    // Matches .tl-gutter / .tl-corner in theme.css. Only used as a fallback
    // when the element has not been laid out yet; the real value is
    // measured from the DOM.
    var GUTTER_FALLBACK = 128;

    // ------------------------------------------------------ effect catalogue
    //
    // One entry per type the runtime knows (Effect.Create in Effects.cs).
    // Defaults are the fallbacks Effect.Read uses, so a freshly added effect
    // serialises to exactly what the runtime would produce for a missing
    // field, and a loaded project that omits a field keeps behaving the same.

    var EFFECTS = [
        {
            type: 'clear',
            label: 'Clear',
            tint: '#5B6B85',
            summary: 'Clears the colour buffer, the depth buffer, or both.',
            defaults: function () {
                return { colour: '#000000FF', screen: true, depth: true };
            },
            fields: [
                { key: 'colour', label: 'Colour', kind: 'colour' },
                { key: 'screen', label: 'Clear screen', kind: 'bool' },
                { key: 'depth', label: 'Clear depth', kind: 'bool' }
            ]
        },
        {
            type: 'layer2d',
            label: 'Layer 2D',
            tint: '#0088FF',
            summary: 'A coloured or textured rectangle drawn straight into the frame.',
            defaults: function () {
                return {
                    startColour: '#FFFFFFFF',
                    endColour: '#FFFFFFFF',
                    sourceBlend: 'sourceAlpha',
                    destinationBlend: 'inverseSourceAlpha',
                    texture: -1
                };
            },
            fields: [
                { key: 'startColour', label: 'Start colour', kind: 'colour' },
                { key: 'endColour', label: 'End colour', kind: 'colour' },
                { key: 'sourceBlend', label: 'Source blend', kind: 'blend' },
                { key: 'destinationBlend', label: 'Dest blend', kind: 'blend' },
                { key: 'texture', label: 'Texture', kind: 'texture' }
            ]
        },
        {
            type: 'display3d',
            label: 'Display 3D',
            tint: '#7C5CFF',
            summary: 'Renders a scene through a camera inside the effect rectangle.',
            defaults: function () {
                return {
                    scene: 0,
                    camera: 0,
                    animation: -1,
                    cameraStart: 0,
                    cameraEnd: 1,
                    animationStart: 0,
                    animationEnd: 1,
                    dontSaveCamera: false
                };
            },
            fields: [
                { key: 'scene', label: 'Scene', kind: 'scene' },
                { key: 'animation', label: 'Animation', kind: 'animation', sceneKey: 'scene' },
                { key: 'camera', label: 'Camera', kind: 'int' },
                { key: 'cameraStart', label: 'Camera start', kind: 'float' },
                { key: 'cameraEnd', label: 'Camera end', kind: 'float' },
                { key: 'animationStart', label: 'Anim start', kind: 'float' },
                { key: 'animationEnd', label: 'Anim end', kind: 'float' },
                { key: 'dontSaveCamera', label: 'Keep shake', kind: 'bool' }
            ]
        },
        {
            type: 'rendertotexture',
            label: 'Render to tex',
            tint: '#C08A2E',
            summary: 'Captures the frame into an off-screen slot for a later effect.',
            defaults: function () {
                return { slot: 0 };
            },
            fields: [
                { key: 'slot', label: 'Slot', kind: 'int', min: 0 }
            ]
        },
        {
            type: 'blur',
            label: 'Blur',
            tint: '#3FB6A8',
            summary: 'Layered blur of a captured slot, composited back over the frame.',
            defaults: function () {
                return {
                    startColour: '#CCFFFFFF',
                    endColour: '#CCFFFFFF',
                    sourceBlend: 'one',
                    destinationBlend: 'one',
                    slot: 0,
                    layers: 4,
                    zoom: 0
                };
            },
            fields: [
                { key: 'startColour', label: 'Start colour', kind: 'colour' },
                { key: 'endColour', label: 'End colour', kind: 'colour' },
                { key: 'sourceBlend', label: 'Source blend', kind: 'blend' },
                { key: 'destinationBlend', label: 'Dest blend', kind: 'blend' },
                { key: 'slot', label: 'Slot', kind: 'int', min: 0 },
                { key: 'layers', label: 'Layers', kind: 'int', min: 0, max: 64 },
                { key: 'zoom', label: 'Zoom', kind: 'int' }
            ]
        },
        {
            type: 'feedback',
            label: 'Feedback',
            tint: '#D45B8E',
            summary: 'Feeds the frame back into itself at a slightly different scale.',
            defaults: function () {
                return {
                    startColour: '#E6FFFFFF',
                    endColour: '#E6FFFFFF',
                    sourceBlend: 'one',
                    destinationBlend: 'one',
                    slot: 0,
                    layers: 6,
                    zoom: 8
                };
            },
            fields: [
                { key: 'startColour', label: 'Start colour', kind: 'colour' },
                { key: 'endColour', label: 'End colour', kind: 'colour' },
                { key: 'sourceBlend', label: 'Source blend', kind: 'blend' },
                { key: 'destinationBlend', label: 'Dest blend', kind: 'blend' },
                { key: 'slot', label: 'Slot', kind: 'int', min: 0 },
                { key: 'layers', label: 'Layers', kind: 'int', min: 0, max: 64 },
                { key: 'zoom', label: 'Zoom', kind: 'int' }
            ]
        },
        {
            type: 'contrast',
            label: 'Contrast',
            tint: '#9AA7BD',
            summary: 'Pushes the frame values away from mid-grey.',
            defaults: function () {
                return {
                    startColour: '#FFFFFFFF',
                    endColour: '#FFFFFFFF',
                    sourceBlend: 'sourceAlpha',
                    destinationBlend: 'inverseSourceAlpha',
                    slot: 0,
                    multiply: 0,
                    add: 0
                };
            },
            fields: [
                { key: 'startColour', label: 'Start colour', kind: 'colour' },
                { key: 'endColour', label: 'End colour', kind: 'colour' },
                { key: 'sourceBlend', label: 'Source blend', kind: 'blend' },
                { key: 'destinationBlend', label: 'Dest blend', kind: 'blend' },
                { key: 'slot', label: 'Slot', kind: 'int', min: 0 },
                { key: 'multiply', label: 'Multiply', kind: 'int' },
                { key: 'add', label: 'Add', kind: 'int' }
            ]
        },
        {
            type: 'camerashake',
            label: 'Camera shake',
            tint: '#E0642E',
            summary: 'Offsets the camera for the following 3D effects.',
            defaults: function () {
                return { eye: 0.1, target: 0.1, shakeStart: 1, shakeEnd: 1, frequency: 5 };
            },
            fields: [
                { key: 'eye', label: 'Eye amount', kind: 'float' },
                { key: 'target', label: 'Target amount', kind: 'float' },
                { key: 'shakeStart', label: 'Start strength', kind: 'float' },
                { key: 'shakeEnd', label: 'End strength', kind: 'float' },
                { key: 'frequency', label: 'Frequency', kind: 'int', min: 1 }
            ]
        },
        {
            type: 'particles',
            label: 'Particles',
            tint: '#F0C040',
            summary: 'Advances a scene particle emitters for the current frame.',
            defaults: function () {
                return { scene: 0, animation: 0, animationStart: 0, animationEnd: 1 };
            },
            fields: [
                { key: 'scene', label: 'Scene', kind: 'scene' },
                { key: 'animation', label: 'Animation', kind: 'animation', sceneKey: 'scene' },
                { key: 'animationStart', label: 'Anim start', kind: 'float' },
                { key: 'animationEnd', label: 'Anim end', kind: 'float' }
            ]
        }
    ];

    var BLENDS = [
        'zero', 'one',
        'sourceColour', 'inverseSourceColour',
        'sourceAlpha', 'inverseSourceAlpha',
        'destinationAlpha', 'inverseDestinationAlpha',
        'destinationColour', 'inverseDestinationColour',
        'sourceAlphaSaturation'
    ];

    var SHADINGS = ['unlit', 'flat', 'gouraud'];
    var PRIMITIVES = ['box', 'sphere', 'plane', 'custom'];

    // ================================================================ state

    var project = null;          // live project object, already in file shape
    var selection = null;        // { kind: 'project' | 'effect' | 'node' | 'scene', ... }
    var frame = 0;               // integer playhead, the one the UI shows
    var playPos = 0;             // float playhead, only meaningful while playing
    var playing = false;
    var lastTick = 0;
    var tickHandle = null;
    var dirty = false;
    var theme = 'light';
    var pxPerFrame = 1;
    var collapsed = {};          // sceneId -> true when its node list is folded
    var barMap = {};             // effect index -> { bar: element, num: element }
    var drag = null;
    var toastTimer = null;

    var canvasW = 0;
    var canvasH = 0;
    var dpr = 1;
    var ctx = null;
    var placedTags = [];
    var pendingTags = [];

    var supportsColourInput = false;

    // Host bridge state. The payload is handed over through a global rather
    // than the URL because a project file is far past any sane URL limit.
    var hostPayload = null;
    var hostAlive = false;
    var hostQueue = [];
    var booted = false;

    var dom = {};

    // ================================================================ utilities

    function $(id) {
        return document.getElementById(id);
    }

    function has(obj, key) {
        return !!obj && Object.prototype.hasOwnProperty.call(obj, key);
    }

    function isArray(value) {
        return Object.prototype.toString.call(value) === '[object Array]';
    }

    function isObject(value) {
        return value !== null && typeof value === 'object' && !isArray(value);
    }

    function str(value) {
        return (value === null || typeof value === 'undefined') ? '' : String(value);
    }

    function trimStr(value) {
        return str(value).replace(/^[\s\u00A0]+|[\s\u00A0]+$/g, '');
    }

    function numOr(value, fallback) {
        var n = parseFloat(value);
        return isFinite(n) ? n : fallback;
    }

    function intOr(value, fallback) {
        var n = parseInt(value, 10);
        return isFinite(n) ? n : fallback;
    }

    function clamp(value, low, high) {
        if (value < low) return low;
        if (value > high) return high;
        return value;
    }

    function indexOfRef(array, item) {
        for (var i = 0; i < array.length; i++) {
            if (array[i] === item) return i;
        }
        return -1;
    }

    function el(tag, className, text) {
        var node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== null && typeof text !== 'undefined') setText(node, text);
        return node;
    }

    function setText(node, text) {
        if (!node) return;
        // textContent is missing in IE8 and innerText is unreliable in
        // modern Firefox, so try both in the order that suits each.
        if (typeof node.textContent === 'string') node.textContent = text;
        else node.innerText = text;
    }

    function clearNode(node) {
        if (!node) return;
        while (node.firstChild) node.removeChild(node.firstChild);
    }

    function on(target, type, handler) {
        if (!target) return;
        if (target.addEventListener) {
            target.addEventListener(type, handler, false);
        } else if (target.attachEvent) {
            // IE8 path: normalise the missing event argument.
            target.attachEvent('on' + type, function () {
                return handler(window.event);
            });
        }
    }

    function stopEvent(event) {
        var e = event || window.event;
        if (!e) return e;
        if (e.preventDefault) e.preventDefault();
        else e.returnValue = false;
        if (e.stopPropagation) e.stopPropagation();
        else e.cancelBubble = true;
        return e;
    }

    function pointerX(event) {
        var e = event || window.event;
        if (!e) return 0;
        if (typeof e.clientX === 'number') return e.clientX;
        return 0;
    }

    function contains(parent, child) {
        if (!parent || !child) return false;
        if (parent === child) return true;
        if (parent.contains) return parent.contains(child);
        var node = child.parentNode;
        while (node) {
            if (node === parent) return true;
            node = node.parentNode;
        }
        return false;
    }

    function now() {
        return new Date().getTime();
    }

    function pad(value, width) {
        var s = String(value);
        while (s.length < width) s = '0' + s;
        return s;
    }

    function formatNum(value) {
        var n = parseFloat(value);
        if (!isFinite(n)) return '0';
        if (n === Math.round(n) && Math.abs(n) < 1e15) return String(n);
        return String(Math.round(n * 100000) / 100000);
    }

    // ------------------------------------------------------------ colours

    // The file stores 0xAARRGGBB as "#RRGGBBAA" (ColorRgba.ToHex). Alpha is
    // last, not first, which is the opposite of CSS hex - one more reason to
    // parse explicitly instead of handing the string to the browser.
    function parseColour(value) {
        var s = trimStr(value).replace(/^#/, '');
        var out = { r: 255, g: 255, b: 255, a: 255 };
        var r = NaN;
        var g = NaN;
        var b = NaN;
        var a = NaN;

        if (s.length === 8) {
            r = parseInt(s.substr(0, 2), 16);
            g = parseInt(s.substr(2, 2), 16);
            b = parseInt(s.substr(4, 2), 16);
            a = parseInt(s.substr(6, 2), 16);
        } else if (s.length === 6) {
            r = parseInt(s.substr(0, 2), 16);
            g = parseInt(s.substr(2, 2), 16);
            b = parseInt(s.substr(4, 2), 16);
            a = 255;
        } else if (s.length === 3) {
            r = parseInt(s.charAt(0) + s.charAt(0), 16);
            g = parseInt(s.charAt(1) + s.charAt(1), 16);
            b = parseInt(s.charAt(2) + s.charAt(2), 16);
            a = 255;
        }

        if (isFinite(r)) out.r = clamp(r, 0, 255);
        if (isFinite(g)) out.g = clamp(g, 0, 255);
        if (isFinite(b)) out.b = clamp(b, 0, 255);
        if (isFinite(a)) out.a = clamp(a, 0, 255);
        return out;
    }

    function cssColour(value, alphaScale) {
        var c = parseColour(value);
        var alpha = c.a / 255;
        if (typeof alphaScale === 'number') alpha *= alphaScale;
        return 'rgba(' + c.r + ',' + c.g + ',' + c.b + ',' +
            (Math.round(alpha * 1000) / 1000) + ')';
    }

    function solidColour(value) {
        var c = parseColour(value);
        return 'rgb(' + c.r + ',' + c.g + ',' + c.b + ')';
    }

    function hex6(value) {
        var c = parseColour(value);
        return '#' + twoHex(c.r) + twoHex(c.g) + twoHex(c.b);
    }

    function twoHex(value) {
        var s = value.toString(16).toUpperCase();
        return s.length < 2 ? '0' + s : s;
    }

    function lerpColour(a, b, t) {
        var ca = parseColour(a);
        var cb = parseColour(b);
        return {
            r: Math.round(ca.r + (cb.r - ca.r) * t),
            g: Math.round(ca.g + (cb.g - ca.g) * t),
            b: Math.round(ca.b + (cb.b - ca.b) * t),
            a: Math.round(ca.a + (cb.a - ca.a) * t)
        };
    }

    function colourCssFromRgba(c, alphaScale) {
        var alpha = (c.a / 255);
        if (typeof alphaScale === 'number') alpha *= alphaScale;
        return 'rgba(' + c.r + ',' + c.g + ',' + c.b + ',' +
            (Math.round(alpha * 1000) / 1000) + ')';
    }

    // ================================================================ model

    function findEffectType(type) {
        var name = str(type).toLowerCase();
        for (var i = 0; i < EFFECTS.length; i++) {
            if (EFFECTS[i].type === name) return EFFECTS[i];
        }
        return null;
    }

    function effectLabel(type) {
        var meta = findEffectType(type);
        return meta ? meta.label : str(type);
    }

    function effectTint(type) {
        var meta = findEffectType(type);
        return meta ? meta.tint : '#7A8496';
    }

    function findScene(id) {
        for (var i = 0; i < project.scenes.length; i++) {
            if (intOr(project.scenes[i].id, -1) === id) return project.scenes[i];
        }
        return null;
    }

    function findNode(scene, id) {
        if (!scene) return null;
        for (var i = 0; i < scene.nodes.length; i++) {
            if (intOr(scene.nodes[i].id, -1) === id) return scene.nodes[i];
        }
        return null;
    }

    function vec3(value, fallback) {
        var out = [fallback[0], fallback[1], fallback[2]];
        if (isArray(value)) {
            out[0] = numOr(value[0], fallback[0]);
            out[1] = numOr(value[1], fallback[1]);
            out[2] = numOr(value[2], fallback[2]);
        }
        return out;
    }

    function vec4(value, fallback) {
        var out = [fallback[0], fallback[1], fallback[2], fallback[3]];
        if (isArray(value) && value.length >= 4) {
            out[0] = numOr(value[0], fallback[0]);
            out[1] = numOr(value[1], fallback[1]);
            out[2] = numOr(value[2], fallback[2]);
            out[3] = numOr(value[3], fallback[3]);
        }
        return out;
    }

    function normalizeProject(raw) {
        var p = isObject(raw) ? raw : {};

        // The scalar repairs mirror ProjectFile.Normalize exactly: an
        // out-of-range value is replaced by the default, not clamped to the
        // nearest legal value. Matching it means "open then save" in the
        // editor produces the same document the runtime would have written,
        // instead of silently nudging a broken project to the edge.
        p.format = intOr(p.format, 1);
        p.name = str(p.name) || 'Untitled';
        p.author = str(p.author);

        p.width = intOr(p.width, 1024);
        if (p.width <= 0 || p.width > 8192) p.width = 1024;

        p.height = intOr(p.height, 768);
        if (p.height <= 0 || p.height > 8192) p.height = 768;

        p.fps = intOr(p.fps, 100);
        if (p.fps <= 0 || p.fps > 1000) p.fps = 100;

        p.duration = intOr(p.duration, 600);
        if (p.duration <= 0) p.duration = p.fps * 5;

        // A negative duration is repaired to fps*5 by the block above, so
        // this cannot be zero; guard anyway because every /duration below
        // would produce Infinity.
        if (p.duration < 1) p.duration = 1;

        p.textures = isArray(p.textures) ? p.textures : [];
        p.scenes = isArray(p.scenes) ? p.scenes : [];
        p.timeline = isArray(p.timeline) ? p.timeline : [];

        var i;

        for (i = 0; i < p.scenes.length; i++) normalizeScene(p.scenes[i]);

        var kept = [];

        for (i = 0; i < p.timeline.length; i++) {
            var effect = normalizeEffect(p.timeline[i], p.duration);
            if (effect) kept.push(effect);
        }

        // Deliberately NOT sorted here. The file keeps the authoring order,
        // and effects render in list order, so re-sorting on load would
        // silently rewrite a project and change the composite wherever two
        // effects share a start frame. The runtime's own Normalize sorts
        // after loading, so saving in authoring order costs nothing and
        // makes "open then save" a no-op diff.
        p.timeline = kept;

        return p;
    }

    function normalizeScene(scene) {
        if (!isObject(scene)) return;

        scene.id = intOr(scene.id, 0);
        scene.name = str(scene.name) || 'scene';

        if (!isObject(scene.camera)) scene.camera = {};

        var camera = scene.camera;

        camera.position = vec3(camera.position, [0, 0, -10]);
        camera.target = vec3(camera.target, [0, 0, 0]);
        camera.up = vec3(camera.up, [0, 1, 0]);
        camera.fov = numOr(camera.fov, 45);
        camera.roll = numOr(camera.roll, 0);
        camera.near = numOr(camera.near, 0.1);
        camera.far = numOr(camera.far, 2000);

        if (!isObject(scene.fog)) scene.fog = {};

        scene.fog.enabled = (scene.fog.enabled === true || scene.fog.enabled === 'true');
        scene.fog.colour = str(scene.fog.colour) || '#000000FF';
        scene.fog.start = numOr(scene.fog.start, 0);
        scene.fog.end = numOr(scene.fog.end, 100);

        scene.ambient = str(scene.ambient) || '#FFFFFFFF';
        scene.lightDirection = vec3(scene.lightDirection, [0.4, -0.7, 0.6]);

        if (!isArray(scene.nodes)) scene.nodes = [];
        if (!isArray(scene.animations)) scene.animations = [];
        if (!isArray(scene.cameraTracks)) scene.cameraTracks = [];

        for (var i = 0; i < scene.nodes.length; i++) normalizeNode(scene.nodes[i]);

        // Scene.RebuildHierarchy promotes a node whose parent id does not
        // exist to a root, so a broken file still shows every object. The
        // tree here walks parent ids directly, so it has to repair the same
        // way or those nodes would be invisible in the editor.
        var known = {};

        for (var k = 0; k < scene.nodes.length; k++) known['n' + scene.nodes[k].id] = true;

        for (var j = 0; j < scene.nodes.length; j++) {
            var parent = intOr(scene.nodes[j].parent, -1);

            if (parent >= 0 && !known['n' + parent]) scene.nodes[j].parent = -1;
            if (parent === scene.nodes[j].id) scene.nodes[j].parent = -1;
        }
    }

    function normalizeNode(node) {
        if (!isObject(node)) return;

        node.id = intOr(node.id, 0);
        node.parent = intOr(node.parent, -1);
        node.name = str(node.name);
        node.visible = (node.visible === false || node.visible === 'false') ? false : true;

        node.position = vec3(node.position, [0, 0, 0]);
        node.scale = vec3(node.scale, [1, 1, 1]);

        if (!isArray(node.rotation) || node.rotation.length < 4) {
            node.rotation = [0, 0, 0, 1];
        } else {
            node.rotation = [
                numOr(node.rotation[0], 0),
                numOr(node.rotation[1], 0),
                numOr(node.rotation[2], 0),
                numOr(node.rotation[3], 1)
            ];
        }

        // SceneNode always has a Material in the runtime (ReadMaterial
        // returns a default even when the JSON has none), so the editor
        // materialises one too. Saving it is equivalent, not a change.
        if (!isObject(node.material)) node.material = {};

        var material = node.material;

        material.texture = intOr(material.texture, -1);
        material.environment = intOr(material.environment, -1);
        material.colour = str(material.colour) || '#FFFFFFFF';
        material.sourceBlend = str(material.sourceBlend) || 'sourceAlpha';
        material.destinationBlend = str(material.destinationBlend) || 'inverseSourceAlpha';
        material.shading = str(material.shading) || 'gouraud';
        material.wireframe = (material.wireframe === true || material.wireframe === 'true');
        material.twoSided = (material.twoSided === true || material.twoSided === 'true');
        material.depthWrite = (material.depthWrite === false || material.depthWrite === 'false') ? false : true;
        material.depthTest = (material.depthTest === false || material.depthTest === 'false') ? false : true;
        material.transparent = (material.transparent === true || material.transparent === 'true');
    }

    function normalizeEffect(effect, duration) {
        if (!isObject(effect)) return null;

        var type = str(effect.type).toLowerCase();

        // An unknown type is kept, not dropped: the runtime skips it, but a
        // file written by a newer editor must survive a round trip through
        // this one. The inspector shows it as read-only timing.
        effect.type = type;

        // Same repair order as ProjectFile.Normalize: swap an inverted span
        // first, then clamp into the project. Swapping rather than clamping
        // matters because a span of 80..20 means 20..80, not a zero-length
        // effect parked at 80.
        effect.start = intOr(effect.start, 0);
        effect.end = intOr(effect.end, effect.start);

        if (effect.end < effect.start) {
            var swap = effect.end;
            effect.end = effect.start;
            effect.start = swap;
        }

        if (effect.start < 0) effect.start = 0;
        if (effect.start > duration) effect.start = duration;
        if (effect.end < 0) effect.end = 0;
        if (effect.end > duration) effect.end = duration;
        if (effect.end < effect.start) effect.end = effect.start;

        effect.rect = vec4(effect.rect, [0, 0, VIRTUAL_W, VIRTUAL_H]);
        effect.rectEnd = vec4(effect.rectEnd, effect.rect);

        var meta = findEffectType(type);

        if (!meta) return effect;

        var defaults = meta.defaults();
        var key;

        for (key in defaults) {
            if (!has(defaults, key)) continue;
            if (typeof effect[key] === 'undefined') effect[key] = defaults[key];
        }

        for (var i = 0; i < meta.fields.length; i++) {
            var field = meta.fields[i];
            var value = effect[field.key];

            if (field.kind === 'int') {
                effect[field.key] = intOr(value, defaults[field.key]);
            } else if (field.kind === 'float') {
                effect[field.key] = numOr(value, defaults[field.key]);
            } else if (field.kind === 'bool') {
                effect[field.key] = (value === true || value === 'true');
            } else if (field.kind === 'colour') {
                effect[field.key] = str(value) || defaults[field.key];
            } else if (field.kind === 'blend') {
                effect[field.key] = str(value) || defaults[field.key];
            }
        }

        return effect;
    }

    // Re-sorts the timeline by start frame, the same comparator the engine's
    // ProjectFile.Normalize uses. Called only after the user changes a span,
    // never on load: see normalizeProject for why.
    function sortTimeline(p) {
        (p || project).timeline.sort(function (a, b) {
            return a.start - b.start;
        });
    }

    function isActive(effect, f) {
        return f >= effect.start && f <= effect.end;
    }

    function effectProgress(effect, f) {
        var span = effect.end - effect.start;
        if (span <= 0) return 0;
        return clamp((f - effect.start) / span, 0, 1);
    }

    function effectRect(effect, f) {
        var t = effectProgress(effect, f);
        var a = effect.rect;
        var b = effect.rectEnd;

        return [
            a[0] + (b[0] - a[0]) * t,
            a[1] + (b[1] - a[1]) * t,
            a[2] + (b[2] - a[2]) * t,
            a[3] + (b[3] - a[3]) * t
        ];
    }

    // ------------------------------------------------------------ new project

    function createDefaultProject() {
        var p = {
            format: 1,
            name: 'Untitled',
            author: '',
            width: 1024,
            height: 768,
            fps: 100,
            duration: 600,
            textures: [],
            scenes: [],
            timeline: []
        };

        var scene = {
            id: 1,
            name: 'scene1',
            camera: {
                position: [0, 1.6, -9],
                target: [0, 0.4, 4],
                up: [0, 1, 0],
                fov: 55,
                roll: 0,
                near: 0.1,
                far: 2000
            },
            fog: { enabled: false, colour: '#000000FF', start: 10, end: 60 },
            ambient: '#B4C8E6FF',
            lightDirection: [0.4, -0.7, 0.6],
            nodes: [],
            animations: [],
            cameraTracks: []
        };

        scene.nodes.push({
            id: 1,
            parent: -1,
            name: 'floor',
            visible: true,
            position: [0, -1.5, 0],
            rotation: [0, 0, 0, 1],
            scale: [1, 1, 1],
            mesh: { primitive: 'plane', size: 40, segments: 4, rings: 4 },
            material: {
                texture: -1,
                environment: -1,
                colour: '#1E2C42FF',
                sourceBlend: 'sourceAlpha',
                destinationBlend: 'inverseSourceAlpha',
                shading: 'unlit',
                wireframe: false,
                twoSided: true,
                depthWrite: true,
                depthTest: true,
                transparent: false
            }
        });

        // A parent and a child, so the tree has something to nest.
        scene.nodes.push({
            id: 2,
            parent: -1,
            name: 'pivot',
            visible: true,
            position: [0, 0, 2],
            rotation: [0, 0, 0, 1],
            scale: [1, 1, 1],
            mesh: { primitive: 'box', size: 1.2, segments: 16, rings: 8 },
            material: {
                texture: -1,
                environment: -1,
                colour: '#3FA0FFFF',
                sourceBlend: 'sourceAlpha',
                destinationBlend: 'inverseSourceAlpha',
                shading: 'gouraud',
                wireframe: false,
                twoSided: false,
                depthWrite: true,
                depthTest: true,
                transparent: false
            }
        });

        scene.nodes.push({
            id: 3,
            parent: 2,
            name: 'satellite',
            visible: true,
            position: [2.4, 0.8, 0],
            rotation: [0, 0, 0, 1],
            scale: [0.5, 0.5, 0.5],
            mesh: { primitive: 'sphere', size: 1, segments: 16, rings: 8 },
            material: {
                texture: -1,
                environment: -1,
                colour: '#FFC850FF',
                sourceBlend: 'sourceAlpha',
                destinationBlend: 'inverseSourceAlpha',
                shading: 'gouraud',
                wireframe: false,
                twoSided: false,
                depthWrite: true,
                depthTest: true,
                transparent: false
            }
        });

        scene.nodes.push({
            id: 4,
            parent: -1,
            name: 'sparks',
            visible: true,
            position: [0, -0.8, 4],
            rotation: [0, 0, 0, 1],
            scale: [1, 1, 1],
            material: {
                texture: -1,
                environment: -1,
                colour: '#FFFFFFFF',
                sourceBlend: 'sourceAlpha',
                destinationBlend: 'inverseSourceAlpha',
                shading: 'unlit',
                wireframe: false,
                twoSided: false,
                depthWrite: false,
                depthTest: true,
                transparent: true
            },
            emitter: {
                shape: 'disc',
                maxParticles: 400,
                rate: 10,
                lifetime: 2,
                lifetimeChaos: 0.6,
                speed: 2.2,
                speedChaos: 1.2,
                direction: [0, 1, 0.1],
                spread: 20,
                startSize: 0.3,
                endSize: 0.02,
                sizeChaos: 0.3,
                startColour: '#96DCFFFF',
                endColour: '#005AFF00',
                gravity: [0, -0.6, 0],
                radius: 1.6,
                boxSize: [0.5, 0.5, 0.5],
                spin: 45,
                seed: 20260101
            }
        });

        p.scenes.push(scene);

        p.timeline.push(makeEffect('clear', 0, p.duration, [0, 0, 800, 600], p));
        p.timeline.push(makeEffect('display3d', 0, p.duration, [0, 0, 800, 600], p));
        p.timeline.push(makeEffect('layer2d', 0, p.duration, [0, 470, 800, 600], p));
        p.timeline.push(makeEffect('feedback', 300, p.duration, [0, 0, 800, 600], p));

        // The band is a colour ramp so the preview shows the interpolation
        // working; alpha stays in the last two hex digits.
        p.timeline[2].startColour = '#00000000';
        p.timeline[2].endColour = '#0A1220CC';

        p.timeline[3].startColour = '#FFFFFF60';
        p.timeline[3].endColour = '#FFFFFF20';
        p.timeline[3].layers = 4;
        p.timeline[3].zoom = 6;

        return normalizeProject(p);
    }

    function makeEffect(type, start, end, rect, p) {
        var meta = findEffectType(type);
        var effect = {
            type: type,
            start: start,
            end: end,
            rect: rect.slice(0),
            rectEnd: rect.slice(0)
        };

        if (meta) {
            var defaults = meta.defaults();
            for (var key in defaults) {
                if (has(defaults, key)) effect[key] = defaults[key];
            }
        }

        // Point scene-based effects at the first scene so a new effect is
        // immediately meaningful rather than referencing scene 0 forever.
        if (typeof effect.scene !== 'undefined' && p && p.scenes.length > 0) {
            effect.scene = intOr(p.scenes[0].id, 0);
        }

        return effect;
    }

    // ================================================================ serialisation

    function serializeProject() {
        // The key order mirrors ProjectSerializer.ToJson so a diff between
        // a file written by the editor and one written by the runtime is
        // empty when nothing changed.
        var doc = {
            format: 1,
            name: project.name,
            author: project.author,
            width: project.width,
            height: project.height,
            fps: project.fps,
            duration: project.duration,
            textures: project.textures,
            scenes: project.scenes,
            timeline: project.timeline
        };

        return JSON.stringify(doc, null, 2);
    }

    function safeFileName(name) {
        var base = trimStr(name).replace(/[\\\/:*?"<>|]+/g, '_');
        if (!base) base = 'project';
        return base + '.dptproj';
    }

    // ================================================================ host bridge
    //
    // The host drives the page with dptReceive(verb, payload) and the page
    // drives the host by parking a payload in a global and navigating to
    // dptengine://<verb>. The payload is not in the URL because a project is
    // megabytes; the host reads it synchronously from the DOM object.

    window.dptReceive = function (verb, payload) {
        if (!booted) {
            hostQueue.push([verb, payload]);
            return;
        }
        handleHost(verb, payload);
    };

    window.dptengineTakePayload = function () {
        var value = hostPayload;
        hostPayload = null;
        return value === null ? '' : value;
    };

    function hostCall(verb, payload) {
        hostPayload = (payload === null || typeof payload === 'undefined') ? '' : String(payload);
        try {
            window.location.href = 'dptengine://' + verb;
        } catch (error) {
            // A browser without a handler for the scheme throws or ignores
            // this; neither is fatal for the editor.
            hostPayload = null;
        }
    }

    function handleHost(verb, payload) {
        hostAlive = true;

        var name = str(verb).toLowerCase();
        var text = str(payload);

        if (name === 'ready') {
            setStatus('Host connected.', 'ok');
            return;
        }

        if (name === 'project') {
            if (!text) {
                setStatus('Host sent an empty project.', 'err');
                return;
            }
            loadText(text, 'host');
            return;
        }

        if (name === 'saved') {
            dirty = false;
            updateStatusRight();
            setStatus(text || 'Saved.', 'ok');
            return;
        }

        if (name === 'error') {
            fail(text || 'The host reported an error.');
            return;
        }

        if (name === 'status') {
            setStatus(text || 'Ready.');
            return;
        }

        setStatus('Unknown host verb "' + str(verb) + '".', 'err');
    }

    function flushHostQueue() {
        var queue = hostQueue;
        hostQueue = [];
        for (var i = 0; i < queue.length; i++) {
            handleHost(queue[i][0], queue[i][1]);
        }
    }

    // ================================================================ tree

    function renderTree() {
        var host = dom.tree;

        clearNode(host);

        if (!project.scenes.length) {
            host.appendChild(el('div', 'empty', 'No scenes in this project.'));
            return;
        }

        for (var i = 0; i < project.scenes.length; i++) {
            var scene = project.scenes[i];
            var open = !collapsed['s' + scene.id];
            var row = el('div', 'tree-row scene');

            row.style.paddingLeft = '4px';

            var twisty = el('span', 'twisty', open ? '\u25BE' : '\u25B8');
            var glyph = el('span', 'glyph', '\u25A6');
            var label = el('span', 'tree-name', scene.name);
            var ident = el('span', 'tree-id', '#' + scene.id);

            row.appendChild(twisty);
            row.appendChild(glyph);
            row.appendChild(label);
            row.appendChild(ident);

            if (isSelectedScene(scene.id)) row.className += ' sel';

            on(row, 'click', (function (target, twist) {
                return function (event) {
                    var e = event || window.event;
                    var source = e.target || e.srcElement;
                    if (source === twist) {
                        var key = 's' + target.id;
                        collapsed[key] = !collapsed[key];
                        renderTree();
                        return;
                    }
                    selectScene(target.id);
                };
            })(scene, twisty));

            host.appendChild(row);

            if (open) {
                var visited = {};
                appendNodeRows(host, scene, -1, 1, visited, 0);
            }
        }
    }

    function appendNodeRows(host, scene, parentId, depth, visited, guard) {
        if (guard > 24) return; // a malformed file must not recurse forever

        for (var i = 0; i < scene.nodes.length; i++) {
            var node = scene.nodes[i];

            if (intOr(node.parent, -1) !== parentId) continue;

            var key = 'n' + node.id;

            if (visited[key]) continue;
            visited[key] = true;

            var row = el('div', 'tree-row');

            row.style.paddingLeft = (4 + depth * 13) + 'px';

            var glyphText = node.emitter ? '\u2726' : (node.mesh ? '\u25A3' : '\u25C7');
            var glyph = el('span', 'glyph', glyphText);
            var label = el('span', 'tree-name', node.name || ('node ' + node.id));
            var kind = el('span', 'tree-kind', node.emitter ? 'emitter' : (node.mesh ? meshName(node) : 'empty'));
            var ident = el('span', 'tree-id', '#' + node.id);

            row.appendChild(el('span', 'twisty', ''));
            row.appendChild(glyph);
            row.appendChild(label);
            row.appendChild(kind);
            row.appendChild(ident);

            if (!node.visible) row.style.opacity = '0.5';
            if (isSelectedNode(scene.id, node.id)) row.className += ' sel';

            on(row, 'click', (function (sceneId, nodeId) {
                return function () { selectNode(sceneId, nodeId); };
            })(scene.id, node.id));

            host.appendChild(row);

            appendNodeRows(host, scene, node.id, depth + 1, visited, guard + 1);
        }
    }

    function meshName(node) {
        if (!node.mesh || typeof node.mesh.primitive !== 'string') return 'mesh';
        return node.mesh.primitive;
    }

    function isSelectedScene(id) {
        return selection && selection.kind === 'scene' && selection.sceneId === id;
    }

    function isSelectedNode(sceneId, nodeId) {
        return selection && selection.kind === 'node' &&
            selection.sceneId === sceneId && selection.nodeId === nodeId;
    }

    function isSelectedEffect(effect) {
        return selection && selection.kind === 'effect' && selection.effect === effect;
    }

    // ================================================================ timeline

    function renderTimeline() {
        var gutter = dom.tlGutter;
        var lanes = dom.tlLanes;

        clearNode(gutter);
        clearNode(lanes);
        barMap = {};

        var list = project.timeline;

        if (!list.length) {
            lanes.appendChild(el('div', 'tl-empty',
                'No effects yet. Use "+ Add effect" to place one at the playhead.'));
        }

        for (var i = 0; i < list.length; i++) {
            var effect = list[i];

            var grow = el('div', 'tl-grow');
            var dot = el('span', 'g-dot');
            var name = el('span', 'g-name', effectLabel(effect.type));
            var num = el('span', 'g-num', effect.start + '\u2013' + effect.end);

            dot.style.backgroundColor = effectTint(effect.type);

            grow.appendChild(dot);
            grow.appendChild(name);
            grow.appendChild(num);

            if (isSelectedEffect(effect)) grow.className += ' sel';

            on(grow, 'click', (function (target) {
                return function () { selectEffect(target); };
            })(effect));

            gutter.appendChild(grow);

            var lane = el('div', 'tl-lane' + (i % 2 ? ' alt' : ''));
            var bar = el('div', 'tl-bar');
            var barLabel = el('span', 'bar-label', effectLabel(effect.type));
            var handleLeft = el('div', 'tl-handle l');
            var handleRight = el('div', 'tl-handle r');

            bar.style.backgroundColor = effectTint(effect.type);

            if (isSelectedEffect(effect)) bar.className += ' sel';

            bar.appendChild(barLabel);
            bar.appendChild(handleLeft);
            bar.appendChild(handleRight);

            on(bar, 'mousedown', (function (target, leftHandle, rightHandle) {
                return function (event) {
                    var e = event || window.event;
                    var source = e.target || e.srcElement;

                    if (source === leftHandle) beginDrag('resize-l', target, e);
                    else if (source === rightHandle) beginDrag('resize-r', target, e);
                    else beginDrag('move', target, e);
                };
            })(effect, handleLeft, handleRight));

            on(lane, 'mousedown', function (event) {
                beginDrag('scrub', null, event || window.event);
            });

            lane.appendChild(bar);
            lanes.appendChild(lane);

            barMap[i] = { bar: bar, num: num, label: barLabel };
        }

        measureTimeline();

        for (var k = 0; k < list.length; k++) layoutBar(list[k], k);

        renderRuler();
        updatePlayhead();
    }

    function measureTimeline() {
        var lanes = dom.tlLanes;
        var width = lanes.clientWidth || 0;

        if (width <= 0) width = 400;

        pxPerFrame = width / Math.max(1, project.duration);

        // The ruler lives outside the scrolling lane area, so when the
        // vertical scrollbar appears the ruler would be wider than the
        // lanes by exactly its width. Reserve the same space here.
        var scroll = dom.tlScroll;
        var scrollbar = 0;

        if (scroll) {
            scrollbar = scroll.offsetWidth - scroll.clientWidth;
            if (scrollbar < 0 || scrollbar > 24) scrollbar = 0;
        }

        if (dom.tlRulerWrap) {
            dom.tlRulerWrap.style.paddingRight = scrollbar + 'px';
        }

        // Let the lane strip fill the viewport so the empty area under the
        // last track is still a scrub surface.
        if (scroll && lanes) {
            var visible = scroll.clientHeight - 22;

            if (visible > 0) lanes.style.minHeight = visible + 'px';
        }
    }

    function layoutBar(effect, index) {
        var entry = barMap[index];

        if (!entry) return;

        var width = Math.max(3, (effect.end - effect.start) * pxPerFrame);

        entry.bar.style.left = (effect.start * pxPerFrame) + 'px';
        entry.bar.style.width = width + 'px';

        if (entry.num) {
            setText(entry.num, effect.start + '\u2013' + effect.end);
        }

        if (entry.label) {
            // A short bar clips its own text mid-word, which reads as a bug.
            // Below ~52px there is no room for a legible name, so the bar
            // keeps only its colour and its tooltip.
            var text = '';

            if (width > 78) text = effectLabel(effect.type) + '  ' + (effect.end - effect.start) + 'f';
            else if (width > 52) text = effectLabel(effect.type);

            setText(entry.label, text);
            entry.bar.title = effectLabel(effect.type) + ' \u00B7 ' +
                effect.start + '\u2013' + effect.end + ' (' + (effect.end - effect.start) + 'f)';
        }
    }

    function chooseStep(pixelsPerFrame, minimumPixels) {
        var steps = [1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000];

        for (var i = 0; i < steps.length; i++) {
            if (steps[i] * pixelsPerFrame >= minimumPixels) return steps[i];
        }

        return steps[steps.length - 1];
    }

    function renderRuler() {
        var ruler = dom.tlRuler;

        clearNode(ruler);

        var step = chooseStep(pxPerFrame, 56);
        var minor = step / 5;
        var f;

        if (minor >= 1 && minor * pxPerFrame >= 6) {
            for (f = 0; f <= project.duration; f += minor) {
                if (f % step === 0) continue;
                var minorTick = el('div', 'tick minor');
                minorTick.style.left = (f * pxPerFrame) + 'px';
                ruler.appendChild(minorTick);
            }
        }

        for (f = 0; f <= project.duration; f += step) {
            var tick = el('div', 'tick');
            var label = el('span', null, String(f));

            tick.style.left = (f * pxPerFrame) + 'px';

            // The last tick sits exactly on the right edge, where a label to
            // its right would be clipped by the ruler's overflow:hidden.
            if ((project.duration - f) * pxPerFrame < 34) label.className = 'tick-end';

            tick.appendChild(label);
            ruler.appendChild(tick);
        }
    }

    function updatePlayhead() {
        if (!dom.tlPlayhead) return;

        var gutter = dom.tlLanes && dom.tlLanes.offsetLeft ? dom.tlLanes.offsetLeft : GUTTER_FALLBACK;

        dom.tlPlayhead.style.left = (gutter + frame * pxPerFrame) + 'px';

        setText(dom.tlFrame, String(frame));
        setText(dom.frameNow, pad(frame, 4));
        setText(dom.frameTotal, String(project.duration));
        setText(dom.tlTotal, String(project.duration));
    }

    // ------------------------------------------------------------ dragging

    function beginDrag(mode, effect, event) {
        stopEvent(event);

        var rect = dom.tlLanes.getBoundingClientRect();
        var x = pointerX(event);

        drag = {
            mode: mode,
            effect: effect,
            x0: x,
            left: rect.left,
            start0: effect ? effect.start : 0,
            end0: effect ? effect.end : 0,
            // A press that never moves must not dirty the project; the flag
            // is only raised once the effect's span actually changes.
            moved: false
        };

        // Repainting the whole timeline here would remove the element that
        // is about to receive the drag, so selection is applied in place.
        if (effect) selectEffect(effect, true);

        if (mode === 'scrub') seekFromPointer(x);

        document.body.style.cursor = (mode === 'move') ? 'move' : 'ew-resize';
    }

    function seekFromPointer(clientX) {
        var x = clientX - drag.left;
        setFrame(Math.round(x / pxPerFrame));
    }

    function handleDragMove(event) {
        if (!drag) return;

        var x = pointerX(event);

        if (drag.mode === 'scrub') {
            seekFromPointer(x);
            return;
        }

        var effect = drag.effect;

        if (!effect) return;

        var delta = Math.round((x - drag.x0) / pxPerFrame);
        var startBefore = effect.start;
        var endBefore = effect.end;

        if (drag.mode === 'move') {
            var length = drag.end0 - drag.start0;
            var start = clamp(drag.start0 + delta, 0, Math.max(0, project.duration - length));
            effect.start = start;
            effect.end = start + length;
        } else if (drag.mode === 'resize-l') {
            effect.start = clamp(drag.start0 + delta, 0, effect.end);
        } else if (drag.mode === 'resize-r') {
            effect.end = clamp(drag.end0 + delta, effect.start, project.duration);
        }

        if (effect.start !== startBefore || effect.end !== endBefore) drag.moved = true;

        layoutBar(effect, indexOfRef(project.timeline, effect));
        renderPreview();
    }

    function endDrag() {
        if (!drag) return;

        var mode = drag.mode;
        var moved = drag.moved;

        drag = null;
        document.body.style.cursor = '';

        if (mode === 'scrub' || !moved) return;

        // Re-sorting after the drag is what keeps the track order equal to
        // the order the runtime plays, but it renumbers the lanes, so the
        // whole timeline is rebuilt once here rather than per mousemove.
        sortTimeline();
        touch();
        renderTimeline();
        renderInspector();
    }

    // ------------------------------------------------------------ editing

    function selectEffect(effect, inPlace) {
        selection = { kind: 'effect', effect: effect };

        if (inPlace) markSelection();
        else renderTimeline();

        renderInspector();
    }

    // Re-applies only the selection classes, for callers that must not
    // rebuild the timeline (drag start, click-to-select on a bar).
    function markSelection() {
        var i;

        for (i = 0; i < project.timeline.length; i++) {
            var entry = barMap[i];

            if (!entry || !entry.bar) continue;

            var on = isSelectedEffect(project.timeline[i]);

            entry.bar.className = 'tl-bar' + (on ? ' sel' : '');
        }

        var rows = dom.tlGutter ? dom.tlGutter.childNodes : [];

        for (i = 0; i < rows.length; i++) {
            if (rows[i].nodeType !== 1) continue;

            var rowOn = isSelectedEffect(project.timeline[i]);

            rows[i].className = 'tl-grow' + (rowOn ? ' sel' : '');
        }
    }

    function selectNode(sceneId, nodeId) {
        selection = { kind: 'node', sceneId: sceneId, nodeId: nodeId };
        renderTree();
        renderInspector();
    }

    function selectScene(sceneId) {
        selection = { kind: 'scene', sceneId: sceneId };
        renderTree();
        renderInspector();
    }

    function selectProject() {
        selection = { kind: 'project' };
        renderTree();
        renderInspector();
    }

    function addEffect(type) {
        var meta = findEffectType(type);

        if (!meta) return;

        var span = Math.max(10, Math.round(project.duration / 6));
        var start = clamp(frame, 0, Math.max(0, project.duration - 1));
        var end = Math.min(project.duration, start + span);

        if (end <= start) {
            start = Math.max(0, project.duration - span);
            end = project.duration;
        }

        var effect = makeEffect(type, start, end, [0, 0, VIRTUAL_W, VIRTUAL_H], project);

        project.timeline.push(effect);
        sortTimeline();

        selectEffect(effect);
        touch();
        renderTimeline();
        setStatus('Added ' + meta.label + ' at frame ' + start + '.');
    }

    function deleteSelectedEffect() {
        if (!selection || selection.kind !== 'effect') return;

        var index = indexOfRef(project.timeline, selection.effect);

        if (index < 0) return;

        var label = effectLabel(project.timeline[index].type);

        project.timeline.splice(index, 1);
        selection = { kind: 'project' };

        touch();
        renderTimeline();
        renderTree();
        renderInspector();
        setStatus('Deleted ' + label + '.');
    }

    function touch() {
        dirty = true;
        renderPreview();
        updateStatusRight();
    }

    // ================================================================ inspector

    function renderInspector() {
        var host = dom.inspector;

        clearNode(host);

        var sel = selection || { kind: 'project' };

        dom.btnDelete.className = (sel.kind === 'effect') ? 'mini' : 'mini hidden';

        if (sel.kind === 'effect') {
            if (indexOfRef(project.timeline, sel.effect) < 0) {
                selection = { kind: 'project' };
                renderInspector();
                return;
            }

            setText(dom.inspectorTitle, 'Effect \u00B7 ' + effectLabel(sel.effect.type));
            buildEffectInspector(host, sel.effect);
            return;
        }

        if (sel.kind === 'node') {
            var scene = findScene(sel.sceneId);
            var node = findNode(scene, sel.nodeId);

            if (!node) {
                selection = { kind: 'project' };
                renderInspector();
                return;
            }

            setText(dom.inspectorTitle, 'Node \u00B7 ' + (node.name || ('node ' + node.id)));
            buildNodeInspector(host, scene, node);
            return;
        }

        if (sel.kind === 'scene') {
            var sceneOnly = findScene(sel.sceneId);

            if (!sceneOnly) {
                selection = { kind: 'project' };
                renderInspector();
                return;
            }

            setText(dom.inspectorTitle, 'Scene \u00B7 ' + sceneOnly.name);
            buildSceneInspector(host, sceneOnly);
            return;
        }

        setText(dom.inspectorTitle, 'Project');
        buildProjectInspector(host);
    }

    function section(title) {
        var sec = el('div', 'sec');
        sec.appendChild(el('div', 'sec-head', title));

        var body = el('div', 'sec-body');

        sec.appendChild(body);
        dom.inspector.appendChild(sec);

        return body;
    }

    function inspectorRow(parent, label, control, hint) {
        var row = el('div', 'irow');
        var cell = el('div', 'icell');

        row.appendChild(el('div', 'ilabel', label));
        cell.appendChild(control);

        if (hint) cell.appendChild(el('div', 'ihint', hint));

        row.appendChild(cell);
        parent.appendChild(row);

        return cell;
    }

    function kvRow(parent, key, value) {
        var row = el('div', 'kv');
        row.appendChild(el('span', 'k', key));
        row.appendChild(el('span', 'v', value));
        parent.appendChild(row);
        return row;
    }

    // ------------------------------------------------------------ controls

    function ctlText(value, commit, placeholder) {
        var input = document.createElement('input');

        input.className = 'ctl';
        input.setAttribute('type', 'text');
        input.setAttribute('spellcheck', 'false');
        input.setAttribute('autocomplete', 'off');
        input.value = str(value);

        if (placeholder) input.setAttribute('placeholder', placeholder);

        on(input, 'change', function () {
            commit(input.value);
        });

        return input;
    }

    function ctlNumber(value, commit, options) {
        var opts = options || {};
        var input = document.createElement('input');

        input.className = 'ctl';
        input.setAttribute('type', 'number');

        if (typeof opts.step !== 'undefined') input.setAttribute('step', String(opts.step));
        if (typeof opts.min !== 'undefined') input.setAttribute('min', String(opts.min));
        if (typeof opts.max !== 'undefined') input.setAttribute('max', String(opts.max));

        input.value = formatNum(value);

        on(input, 'change', function () {
            var parsed = parseFloat(input.value);

            if (!isFinite(parsed)) {
                input.value = formatNum(value);
                return;
            }

            if (typeof opts.min === 'number') parsed = Math.max(opts.min, parsed);
            if (typeof opts.max === 'number') parsed = Math.min(opts.max, parsed);

            input.value = formatNum(parsed);
            commit(parsed);
        });

        return input;
    }

    function ctlBool(value, commit) {
        var input = document.createElement('input');

        input.className = 'check';
        input.setAttribute('type', 'checkbox');

        if (value) input.checked = true;

        on(input, 'click', function () {
            commit(!!input.checked);
        });

        return input;
    }

    function ctlSelect(values, labels, value, commit) {
        var select = document.createElement('select');

        select.className = 'ctl';

        for (var i = 0; i < values.length; i++) {
            var option = document.createElement('option');
            option.value = str(values[i]);
            option.text = str(labels ? labels[i] : values[i]);
            select.appendChild(option);
        }

        select.value = str(value);

        on(select, 'change', function () {
            commit(select.value);
        });

        return select;
    }

    function ctlColour(value, commit) {
        var wrap = el('div', 'colctl');
        var swatch = el('div', 'swatch');
        var text = document.createElement('input');
        var current = str(value);

        text.className = 'ctl mono';
        text.setAttribute('type', 'text');
        text.setAttribute('spellcheck', 'false');
        text.value = current;

        function paint(next) {
            current = next;

            // Solid first, then rgba: IE8 drops the second declaration and
            // keeps the flat colour instead of showing nothing.
            swatch.style.backgroundColor = solidColour(current);
            swatch.style.backgroundColor = cssColour(current);
            text.value = current;

            if (picker) picker.value = hex6(current);
        }

        on(text, 'change', function () {
            var next = trimStr(text.value).toUpperCase();

            if (next.charAt(0) !== '#') next = '#' + next;

            if (!/^#[0-9A-F]{6}([0-9A-F]{2})?$/.test(next)) {
                text.value = current;
                return;
            }

            if (next.length === 7) next += 'FF';

            paint(next);
            commit(next);
        });

        var picker = null;

        if (supportsColourInput) {
            picker = document.createElement('input');
            picker.setAttribute('type', 'color');
            picker.value = hex6(current);

            on(picker, 'change', function () {
                // The picker only speaks #RRGGBB; keep the alpha we had.
                var alpha = current.length === 9 ? current.substr(7, 2) : 'FF';
                var next = str(picker.value).toUpperCase() + alpha;

                if (!/^#[0-9A-F]{6}[0-9A-F]{2}$/.test(next)) return;

                paint(next);
                commit(next);
            });
        }

        paint(current);

        wrap.appendChild(swatch);
        wrap.appendChild(text);

        if (picker) wrap.appendChild(picker);

        return wrap;
    }

    function ctlQuad(values, commit, labels, wide) {
        var wrap = el('div', 'quad' + (wide ? ' wide' : ''));

        for (var i = 0; i < values.length; i++) {
            (function (index) {
                var input = document.createElement('input');

                input.className = 'q';
                input.value = formatCompact(values[index]);

                if (labels) input.title = labels[index];

                on(input, 'change', function () {
                    var parsed = parseFloat(input.value);

                    if (!isFinite(parsed)) {
                        input.value = formatCompact(values[index]);
                        return;
                    }

                    values[index] = parsed;
                    input.value = formatCompact(parsed);
                    commit();
                });

                wrap.appendChild(input);
            })(i);
        }

        return wrap;
    }

    // Three significant decimals, no trailing zeros: a quaternion component
    // is 0.30931..., which does not fit in a 33px box and would be clipped
    // to something that looks like garbage.
    function formatCompact(value) {
        var n = parseFloat(value);

        if (!isFinite(n)) return '0';
        if (n === Math.round(n) && Math.abs(n) < 1e9) return String(n);

        var rounded = Math.round(n * 1000) / 1000;

        if (rounded === Math.round(rounded)) return String(Math.round(rounded));

        return String(rounded);
    }

    // ------------------------------------------------------------ effect

    function buildEffectInspector(host, effect) {
        var meta = findEffectType(effect.type);
        var body;

        body = section('Timing');

        inspectorRow(body, 'Start', ctlNumber(effect.start, function (value) {
            effect.start = clamp(Math.round(value), 0, effect.end);
            afterTimingEdit(effect);
        }, { min: 0, max: project.duration, step: 1 }), 'frame');

        inspectorRow(body, 'End', ctlNumber(effect.end, function (value) {
            effect.end = clamp(Math.round(value), effect.start, project.duration);
            afterTimingEdit(effect);
        }, { min: 0, max: project.duration, step: 1 }), 'frame');

        var seconds = (effect.end - effect.start) / Math.max(1, project.fps);

        body.appendChild(el('div', 'kv', 'Length ' + (effect.end - effect.start) +
            ' frames \u00B7 ' + (Math.round(seconds * 100) / 100) + ' s'));

        body = section('Rectangle (800\u00D7600 space)');

        inspectorRow(body, 'Start', ctlQuad(effect.rect, function () {
            touch();
            renderPreview();
        }, ['x1', 'y1', 'x2', 'y2']), 'x1, y1, x2, y2');

        inspectorRow(body, 'End', ctlQuad(effect.rectEnd, function () {
            touch();
            renderPreview();
        }, ['x1', 'y1', 'x2', 'y2']), 'interpolated over the span');

        if (!meta) {
            var note = el('div', 'note',
                'Unknown effect type "' + effect.type + '". The runtime skips it, ' +
                'and the editor preserves it so a round trip does not lose data.');
            section('Type').appendChild(note);
            return;
        }

        body = section(meta.label);

        body.appendChild(el('div', 'note', meta.summary));

        for (var i = 0; i < meta.fields.length; i++) {
            buildEffectField(body, meta.fields[i], effect);
        }
    }

    function buildEffectField(body, field, effect) {
        var value = effect[field.key];

        if (field.kind === 'int' || field.kind === 'float') {
            inspectorRow(body, field.label, ctlNumber(value, function (next) {
                effect[field.key] = (field.kind === 'int') ? Math.round(next) : next;
                touch();
            }, { step: field.kind === 'int' ? 1 : 0.01, min: field.min, max: field.max }), null);
            return;
        }

        if (field.kind === 'bool') {
            inspectorRow(body, field.label, ctlBool(value, function (next) {
                effect[field.key] = next;
                touch();
            }));
            return;
        }

        if (field.kind === 'colour') {
            inspectorRow(body, field.label, ctlColour(value, function (next) {
                effect[field.key] = next;
                touch();
            }), '#RRGGBBAA');
            return;
        }

        if (field.kind === 'blend') {
            inspectorRow(body, field.label, ctlSelect(BLENDS, BLENDS, value, function (next) {
                effect[field.key] = next;
                touch();
            }));
            return;
        }

        if (field.kind === 'scene') {
            var ids = [];
            var labels = [];

            for (var i = 0; i < project.scenes.length; i++) {
                ids.push(project.scenes[i].id);
                labels.push('#' + project.scenes[i].id + ' ' + project.scenes[i].name);
            }

            if (!ids.length) {
                ids.push(0);
                labels.push('(no scenes)');
            }

            inspectorRow(body, field.label, ctlSelect(ids, labels, value, function (next) {
                effect[field.key] = intOr(next, 0);
                touch();
                renderInspector(); // the animation list depends on the scene
            }));
            return;
        }

        if (field.kind === 'animation') {
            var scene = findScene(intOr(effect[field.sceneKey], -1));
            var animIds = [-1];
            var animLabels = ['-1 (none)'];

            if (scene) {
                for (var a = 0; a < scene.animations.length; a++) {
                    animIds.push(scene.animations[a].id);
                    animLabels.push('#' + scene.animations[a].id + ' ' + scene.animations[a].name);
                }
            }

            inspectorRow(body, field.label, ctlSelect(animIds, animLabels, value, function (next) {
                effect[field.key] = intOr(next, -1);
                touch();
            }), scene ? (scene.animations.length + ' in scene') : 'no scene selected');
            return;
        }

        if (field.kind === 'texture') {
            var textureIds = [-1];
            var textureLabels = ['-1 (none)'];

            for (var t = 0; t < project.textures.length; t++) {
                textureIds.push(project.textures[t].id);
                textureLabels.push('#' + project.textures[t].id);
            }

            inspectorRow(body, field.label, ctlSelect(textureIds, textureLabels, value, function (next) {
                effect[field.key] = intOr(next, -1);
                touch();
            }), project.textures.length ? null : 'project has no textures');
            return;
        }
    }

    function afterTimingEdit(effect) {
        // A changed span can reorder the timeline, so both the lanes and the
        // gutter have to be rebuilt; touch() repaints the preview.
        sortTimeline();
        renderTimeline();
        renderInspector();
        touch();
    }

    // ------------------------------------------------------------ node

    function buildNodeInspector(host, scene, node) {
        var body = section('Node');
        var identity = el('div', 'kv');

        identity.appendChild(el('span', 'k', 'Scene'));
        identity.appendChild(el('span', 'v', scene.name + ' (#' + scene.id + ')'));
        body.appendChild(identity);

        kvRow(body, 'Id', String(node.id));
        kvRow(body, 'Parent', (intOr(node.parent, -1) < 0) ? '(root)' : ('#' + node.parent));

        inspectorRow(body, 'Name', ctlText(node.name, function (value) {
            node.name = value;
            renderTree();
            touch();
        }));

        inspectorRow(body, 'Visible', ctlBool(node.visible, function (value) {
            node.visible = value;
            renderTree();
            touch();
        }));

        body = section('Transform');

        inspectorRow(body, 'Position', ctlQuad(node.position, function () {
            touch();
        }, ['x', 'y', 'z']));

        inspectorRow(body, 'Rotation', ctlQuad(node.rotation, function () {
            touch();
        }, ['x', 'y', 'z', 'w'], true), 'quaternion; the runtime normalises it');

        inspectorRow(body, 'Scale', ctlQuad(node.scale, function () {
            touch();
        }, ['x', 'y', 'z']));

        var material = node.material;

        body = section('Material');

        inspectorRow(body, 'Colour', ctlColour(material.colour, function (value) {
            material.colour = value;
            touch();
        }), '#RRGGBBAA');

        inspectorRow(body, 'Texture', ctlNumber(material.texture, function (value) {
            material.texture = Math.round(value);
            touch();
        }, { step: 1 }), '-1 for untextured');

        inspectorRow(body, 'Environment', ctlNumber(material.environment, function (value) {
            material.environment = Math.round(value);
            touch();
        }, { step: 1 }));

        inspectorRow(body, 'Source blend', ctlSelect(BLENDS, BLENDS, material.sourceBlend, function (value) {
            material.sourceBlend = value;
            touch();
        }));

        inspectorRow(body, 'Dest blend', ctlSelect(BLENDS, BLENDS, material.destinationBlend, function (value) {
            material.destinationBlend = value;
            touch();
        }));

        inspectorRow(body, 'Shading', ctlSelect(SHADINGS, SHADINGS, material.shading, function (value) {
            material.shading = value;
            touch();
        }));

        inspectorRow(body, 'Wireframe', ctlBool(material.wireframe, function (value) {
            material.wireframe = value;
            touch();
        }));

        inspectorRow(body, 'Two sided', ctlBool(material.twoSided, function (value) {
            material.twoSided = value;
            touch();
        }));

        inspectorRow(body, 'Depth write', ctlBool(material.depthWrite, function (value) {
            material.depthWrite = value;
            touch();
        }));

        inspectorRow(body, 'Depth test', ctlBool(material.depthTest, function (value) {
            material.depthTest = value;
            touch();
        }));

        inspectorRow(body, 'Transparent', ctlBool(material.transparent, function (value) {
            material.transparent = value;
            touch();
        }));

        if (node.mesh) {
            body = section('Mesh');

            inspectorRow(body, 'Primitive', ctlSelect(PRIMITIVES, PRIMITIVES,
                str(node.mesh.primitive) || 'box', function (value) {
                    node.mesh.primitive = value;
                    renderTree();
                    touch();
                }));

            inspectorRow(body, 'Size', ctlNumber(numOr(node.mesh.size, 2), function (value) {
                node.mesh.size = value;
                touch();
            }, { step: 0.1 }));

            inspectorRow(body, 'Segments', ctlNumber(intOr(node.mesh.segments, 16), function (value) {
                node.mesh.segments = Math.round(value);
                touch();
            }, { step: 1, min: 1 }));

            inspectorRow(body, 'Rings', ctlNumber(intOr(node.mesh.rings, 8), function (value) {
                node.mesh.rings = Math.round(value);
                touch();
            }, { step: 1, min: 1 }));

            if (str(node.mesh.primitive) === 'custom') {
                body.appendChild(el('div', 'note',
                    'Custom geometry is stored as a vertex list. This editor shows ' +
                    'its parameters but does not edit the vertices.'));
            }
        }

        if (node.emitter) {
            body = section('Emitter (read only)');

            kvRow(body, 'Shape', str(node.emitter.shape));
            kvRow(body, 'Max', String(node.emitter.maxParticles));
            kvRow(body, 'Rate', String(node.emitter.rate));
            kvRow(body, 'Lifetime', String(node.emitter.lifetime));
            kvRow(body, 'Seed', String(node.emitter.seed));

            body.appendChild(el('div', 'note',
                'Emitter fields are preserved on save. Edit them in the project JSON ' +
                'until this panel grows controls.'));
        }
    }

    // ------------------------------------------------------------ scene

    function buildSceneInspector(host, scene) {
        var body = section('Scene');

        kvRow(body, 'Id', String(scene.id));

        inspectorRow(body, 'Name', ctlText(scene.name, function (value) {
            scene.name = value;
            renderTree();
            touch();
        }));

        body = section('Camera');

        inspectorRow(body, 'Position', ctlQuad(scene.camera.position, touch, ['x', 'y', 'z']));
        inspectorRow(body, 'Target', ctlQuad(scene.camera.target, touch, ['x', 'y', 'z']));
        inspectorRow(body, 'Up', ctlQuad(scene.camera.up, touch, ['x', 'y', 'z']));

        inspectorRow(body, 'Field of view', ctlNumber(scene.camera.fov, function (value) {
            scene.camera.fov = value;
            touch();
        }, { step: 1, min: 1, max: 179 }), 'degrees, vertical');

        inspectorRow(body, 'Roll', ctlNumber(scene.camera.roll, function (value) {
            scene.camera.roll = value;
            touch();
        }, { step: 1 }), 'degrees');

        inspectorRow(body, 'Near', ctlNumber(scene.camera.near, function (value) {
            scene.camera.near = value;
            touch();
        }, { step: 0.1 }));

        inspectorRow(body, 'Far', ctlNumber(scene.camera.far, function (value) {
            scene.camera.far = value;
            touch();
        }, { step: 10 }));

        body = section('Fog');

        inspectorRow(body, 'Enabled', ctlBool(scene.fog.enabled, function (value) {
            scene.fog.enabled = value;
            touch();
        }));

        inspectorRow(body, 'Colour', ctlColour(scene.fog.colour, function (value) {
            scene.fog.colour = value;
            touch();
        }), '#RRGGBBAA');

        inspectorRow(body, 'Start', ctlNumber(scene.fog.start, function (value) {
            scene.fog.start = value;
            touch();
        }, { step: 1 }));

        inspectorRow(body, 'End', ctlNumber(scene.fog.end, function (value) {
            scene.fog.end = value;
            touch();
        }, { step: 1 }));

        body = section('Environment');

        inspectorRow(body, 'Ambient', ctlColour(scene.ambient, function (value) {
            scene.ambient = value;
            touch();
        }), '#RRGGBBAA');

        inspectorRow(body, 'Light dir', ctlQuad(scene.lightDirection, touch, ['x', 'y', 'z']));

        body = section('Animations');

        if (!scene.animations.length) {
            body.appendChild(el('div', 'kv', 'No animations in this scene.'));
        }

        for (var i = 0; i < scene.animations.length; i++) {
            var animation = scene.animations[i];
            var tracks = isArray(animation.tracks) ? animation.tracks.length : 0;
            kvRow(body, '#' + animation.id, (animation.name || '(unnamed)') + ' \u00B7 ' + tracks + ' tracks');
        }

        if (isArray(scene.cameraTracks) && scene.cameraTracks.length) {
            kvRow(body, 'camera', scene.cameraTracks.length + ' tracks');
        }

        body = section('Nodes');

        if (!scene.nodes.length) {
            body.appendChild(el('div', 'kv', 'No nodes in this scene.'));
        }

        for (var n = 0; n < scene.nodes.length; n++) {
            var node = scene.nodes[n];
            var entry = kvRow(body, '#' + node.id, node.name || '(unnamed)');

            entry.style.cursor = 'pointer';

            on(entry, 'click', (function (sceneId, nodeId) {
                return function () { selectNode(sceneId, nodeId); };
            })(scene.id, node.id));
        }
    }

    // ------------------------------------------------------------ project

    function buildProjectInspector(host) {
        var body = section('Project');

        inspectorRow(body, 'Name', ctlText(project.name, function (value) {
            project.name = trimStr(value) || 'Untitled';
            syncTopbar();
            touch();
        }));

        inspectorRow(body, 'Author', ctlText(project.author, function (value) {
            project.author = value;
            touch();
        }), 'written to the "author" field');

        body = section('Output');

        inspectorRow(body, 'Width', ctlNumber(project.width, function (value) {
            project.width = clamp(Math.round(value), 16, 8192);
            syncTopbar();
            layout();
            touch();
        }, { step: 1, min: 16, max: 8192 }), 'pixels');

        inspectorRow(body, 'Height', ctlNumber(project.height, function (value) {
            project.height = clamp(Math.round(value), 16, 8192);
            syncTopbar();
            layout();
            touch();
        }, { step: 1, min: 16, max: 8192 }), 'pixels');

        inspectorRow(body, 'Frames/sec', ctlNumber(project.fps, function (value) {
            project.fps = clamp(Math.round(value), 1, 1000);
            syncTopbar();
            renderInspector();
            touch();
        }, { step: 1, min: 1, max: 1000 }));

        inspectorRow(body, 'Duration', ctlNumber(project.duration, function (value) {
            setDuration(clamp(Math.round(value), 1, 1000000));
        }, { step: 1, min: 1 }), 'frames');

        body.appendChild(el('div', 'kv', 'Format ' + project.format + ' \u00B7 ' +
            project.width + '\u00D7' + project.height + ' \u00B7 ' + project.fps + ' fps \u00B7 ' +
            (Math.round((project.duration / Math.max(1, project.fps)) * 100) / 100) + ' s'));

        body = section('Contents');

        kvRow(body, 'Scenes', String(project.scenes.length));
        kvRow(body, 'Effects', String(project.timeline.length));
        kvRow(body, 'Textures', String(project.textures.length));

        var nodes = 0;

        for (var s = 0; s < project.scenes.length; s++) {
            nodes += project.scenes[s].nodes.length;
        }

        kvRow(body, 'Nodes', String(nodes));

        body = section('Textures');

        if (!project.textures.length) {
            body.appendChild(el('div', 'kv', 'No embedded textures.'));
        }

        for (var i = 0; i < project.textures.length; i++) {
            var texture = project.textures[i];
            var pixels = str(texture.pixels).length;
            kvRow(body, '#' + texture.id,
                texture.width + '\u00D7' + texture.height +
                (pixels ? ' \u00B7 ' + Math.round(pixels * 0.75 / 1024) + ' KiB' : ''));
        }

        body.appendChild(el('div', 'note',
            'Textures are stored as base64 ARGB pixels. This editor keeps them ' +
            'verbatim; it does not import or paint new ones.'));
    }

    function setDuration(value) {
        project.duration = value;

        for (var i = 0; i < project.timeline.length; i++) {
            var effect = project.timeline[i];
            if (effect.end > value) effect.end = value;
            if (effect.start > value) effect.start = value;
        }

        frame = clamp(frame, 0, value);
        playPos = clamp(playPos, 0, value);

        syncTopbar();
        layout();
        renderInspector();
        touch();
    }

    // ================================================================ preview
    //
    // The browser cannot run the Direct3D pipeline, so this is a layout
    // schematic: every active effect paints its interpolated rectangle with
    // its own colour, and display3d paints a wireframe box labelled with the
    // scene it will show. It is deliberately not an approximation of the
    // final pixels - it is a map of what covers what, at which frame.

    function sizeCanvas() {
        var canvas = dom.preview;

        if (!canvas || !canvas.getContext) return;

        var frameEl = dom.previewFrame;
        var width = Math.max(48, frameEl.clientWidth);
        var height = Math.max(48, frameEl.clientHeight);

        if (width === canvasW && height === canvasH && ctx) return;

        canvasW = width;
        canvasH = height;
        dpr = window.devicePixelRatio || 1;

        if (dpr > 2) dpr = 2; // a 3x buffer costs more than it shows

        canvas.width = Math.round(width * dpr);
        canvas.height = Math.round(height * dpr);
        canvas.style.width = width + 'px';
        canvas.style.height = height + 'px';

        ctx = canvas.getContext('2d');

        if (ctx && ctx.scale) ctx.scale(dpr, dpr);
    }

    function computeView() {
        var aspect = project.width / Math.max(1, project.height);
        var fw = canvasW - 26;
        var fh = fw / aspect;

        if (fh > canvasH - 26) {
            fh = canvasH - 26;
            fw = fh * aspect;
        }

        if (fw < 16 || fh < 16) {
            fw = Math.max(16, canvasW - 26);
            fh = Math.max(16, canvasH - 26);
        }

        return {
            x: (canvasW - fw) / 2,
            y: (canvasH - fh) / 2,
            w: fw,
            h: fh
        };
    }

    function mapRect(rect, view) {
        var sx = view.w / VIRTUAL_W;
        var sy = view.h / VIRTUAL_H;

        var x1 = view.x + rect[0] * sx;
        var y1 = view.y + rect[1] * sy;
        var x2 = view.x + rect[2] * sx;
        var y2 = view.y + rect[3] * sy;

        // The runtime clamps an inverted or empty rectangle to one pixel so
        // the driver accepts the viewport; two pixels here keeps it visible.
        if (x2 <= x1) x2 = x1 + 2;
        if (y2 <= y1) y2 = y1 + 2;

        return { x1: x1, y1: y1, x2: x2, y2: y2 };
    }

    function renderPreview() {
        if (!ctx) return;

        var view = computeView();
        var i;

        placedTags = [];
        pendingTags = [];

        ctx.clearRect(0, 0, canvasW, canvasH);

        ctx.fillStyle = '#0B0E14';
        ctx.fillRect(0, 0, canvasW, canvasH);

        ctx.save();

        if (ctx.beginPath && ctx.rect && ctx.clip) {
            ctx.beginPath();
            ctx.rect(view.x, view.y, view.w, view.h);
            ctx.clip();
        }

        ctx.fillStyle = '#101722';
        ctx.fillRect(view.x, view.y, view.w, view.h);

        var active = 0;

        for (i = 0; i < project.timeline.length; i++) {
            var effect = project.timeline[i];

            if (!isActive(effect, frame)) continue;

            active++;
            drawEffect(view, effect);
        }

        ctx.restore();

        // Tags are drawn in a second pass, after every effect rectangle has
        // been filled. An effect drawn later covers an earlier effect's
        // rectangle - which is correct - but it must not cover the earlier
        // effect's label, or the preview reads as if that effect is absent.
        // Still clipped to the frame, so a rect outside 0..800 cannot put
        // text on the letterbox.
        if (ctx.beginPath && ctx.rect && ctx.clip) {
            ctx.save();
            ctx.beginPath();
            ctx.rect(view.x, view.y, view.w, view.h);
            ctx.clip();
            flushTags();
            ctx.restore();
        } else {
            flushTags();
        }

        // Frame edge: makes the letterbox obvious and shows the aspect the
        // runtime will letterbox to.
        ctx.strokeStyle = 'rgba(255,255,255,0.16)';
        ctx.lineWidth = 1;
        ctx.strokeRect(view.x + 0.5, view.y + 0.5, view.w - 1, view.h - 1);

        updateHud(active);
    }

    function updateHud(active) {
        setText(dom.hudLeft, 'FRAME ' + pad(frame, 4) + ' \u00B7 ' + active +
            (active === 1 ? ' effect' : ' effects'));

        setText(dom.hudRight, project.width + '\u00D7' + project.height + ' \u00B7 ' +
            project.fps + ' fps');
    }

    function drawEffect(view, effect) {
        var r = mapRect(effectRect(effect, frame), view);
        var width = r.x2 - r.x1;
        var height = r.y2 - r.y1;
        var progress = effectProgress(effect, frame);

        if (width < 1 || height < 1) return;

        var scene;

        switch (effect.type) {
            case 'clear':
                // ClearEffect ignores its rectangle: Device.Clear wipes the
                // whole target. Reproduce that rather than the rect. The
                // colour's alpha is dropped because a clear writes the
                // buffer outright - D3D's Clear ignores alpha, and painting
                // it transparent here would show the previous frame through.
                ctx.fillStyle = solidColour(effect.colour);
                ctx.fillRect(view.x, view.y, view.w, view.h);
                return;

            case 'layer2d':
                ctx.fillStyle = colourCssFromRgba(
                    lerpColour(effect.startColour, effect.endColour, progress));
                ctx.fillRect(r.x1, r.y1, width, height);
                drawTag(r.x1, r.y1, 'LAYER 2D' + (effect.texture >= 0 ? ' #' + effect.texture : ''),
                    width - 4);
                return;

            case 'display3d':
                scene = findScene(intOr(effect.scene, -1));
                ctx.fillStyle = 'rgba(8,12,22,0.92)';
                ctx.fillRect(r.x1, r.y1, width, height);
                clipRect(r, function () {
                    drawFloorGrid(r);
                    drawWireCube(r);

                    // Emitters belong to the scene, so their sparks appear
                    // here and not at the particles effect - which only
                    // advances the simulation and draws nothing itself.
                    if (scene) drawSceneParticles(r, scene, effect, width);
                });
                ctx.strokeStyle = 'rgba(124,92,255,0.9)';
                ctx.lineWidth = 1;
                ctx.strokeRect(r.x1 + 0.5, r.y1 + 0.5, width - 1, height - 1);
                drawTag(r.x1 + 4, r.y1 + 4,
                    'SCENE ' + (scene ? scene.name : '(missing)'), width - 8);
                return;

            case 'rendertotexture':
                ctx.fillStyle = 'rgba(192,138,46,0.16)';
                ctx.fillRect(r.x1, r.y1, width, height);
                dashedRect(r, 'rgba(192,138,46,0.95)');
                drawTag(r.x1 + 4, r.y1 + 4, 'CAPTURE \u2192 SLOT ' + intOr(effect.slot, 0), width - 8);
                return;

            case 'blur':
                if (ctx.createRadialGradient) {
                    var blurGradient = ctx.createRadialGradient(
                        (r.x1 + r.x2) / 2, (r.y1 + r.y2) / 2, 0,
                        (r.x1 + r.x2) / 2, (r.y1 + r.y2) / 2,
                        Math.max(width, height) * 0.62);

                    blurGradient.addColorStop(0, 'rgba(63,182,168,0.30)');
                    blurGradient.addColorStop(1, 'rgba(63,182,168,0.03)');
                    ctx.fillStyle = blurGradient;
                } else {
                    ctx.fillStyle = 'rgba(63,182,168,0.16)';
                }
                ctx.fillRect(r.x1, r.y1, width, height);
                dashedRect(r, 'rgba(63,182,168,0.75)');
                drawTag(r.x1 + 4, r.y1 + 4, 'BLUR \u00D7' + intOr(effect.layers, 0), width - 8);
                return;

            case 'feedback':
                ctx.fillStyle = 'rgba(212,91,142,0.10)';
                ctx.fillRect(r.x1, r.y1, width, height);

                var inset = Math.min(width, height) * 0.07;

                for (var k = 0; k < 4; k++) {
                    var d = inset * (k + 1);

                    if (width - 2 * d < 2 || height - 2 * d < 2) break;

                    ctx.strokeStyle = 'rgba(212,91,142,' + (0.55 - k * 0.12) + ')';
                    ctx.lineWidth = 1;
                    ctx.strokeRect(r.x1 + d, r.y1 + d, width - 2 * d, height - 2 * d);
                }

                drawTag(r.x1 + 4, r.y1 + 4, 'FEEDBACK \u00D7' + intOr(effect.layers, 0), width - 8);
                return;

            case 'contrast':
                ctx.fillStyle = 'rgba(154,167,189,0.13)';
                ctx.fillRect(r.x1, r.y1, width, height);
                dashedRect(r, 'rgba(154,167,189,0.8)');
                drawTag(r.x1 + 4, r.y1 + 4,
                    'CONTRAST ' + (intOr(effect.multiply, 0) >= 0 ? '+' : '') +
                    intOr(effect.multiply, 0) + ' / ' + intOr(effect.add, 0), width - 8);
                return;

            case 'camerashake':
                // A state effect: nothing is drawn, the following 3D pass is
                // displaced. Show the displacement at the frame edges.
                ctx.strokeStyle = 'rgba(224,100,46,0.85)';
                ctx.lineWidth = 2;
                drawShakeCorners(r, frame);
                drawTag(r.x1 + 4, r.y1 + 4,
                    'SHAKE ' + formatNum(effect.eye) + ' / ' + formatNum(effect.target), width - 8);
                return;

            case 'particles':
                // ParticleEffect.Draws is false: it advances a scene's
                // emitters and the 3D pass draws the results. Draw the hint
                // only where it would be visible, i.e. before display3d
                // covers it, and label it as a state change.
                dashedRect(r, 'rgba(240,192,64,0.55)');
                drawTag(r.x1 + 4, r.y1 + 4, 'PARTICLES \u00B7 STATE', width - 8);
                return;

            default:
                ctx.fillStyle = 'rgba(122,132,150,0.12)';
                ctx.fillRect(r.x1, r.y1, width, height);
                dashedRect(r, 'rgba(122,132,150,0.8)');
                drawTag(r.x1 + 4, r.y1 + 4, str(effect.type).toUpperCase(), width - 8);
        }
    }

    function clipRect(r, draw) {
        if (!ctx.save || !ctx.clip) {
            draw();
            return;
        }

        ctx.save();
        ctx.beginPath();
        ctx.rect(r.x1, r.y1, r.x2 - r.x1, r.y2 - r.y1);
        ctx.clip();
        draw();
        ctx.restore();
    }

    function dashedRect(r, colour) {
        ctx.strokeStyle = colour;
        ctx.lineWidth = 1;

        if (ctx.setLineDash) ctx.setLineDash([4, 3]);
        ctx.strokeRect(r.x1 + 0.5, r.y1 + 0.5, r.x2 - r.x1 - 1, r.y2 - r.y1 - 1);
        if (ctx.setLineDash) ctx.setLineDash([]);
    }

    // Effects draw in timeline order, so a later full-frame effect would
    // bury an earlier effect's label. Walk down past any label already
    // placed in this frame instead of letting them stack on one pixel.
    function placeTag(x, y, width) {
        var top = y;
        var guard = 0;

        while (overlapsTag(x, top, width) && guard < 10) {
            top += 15;
            guard++;
        }

        return top;
    }

    function drawTag(x, y, text, maxWidth) {
        if (maxWidth < 26) return;

        pendingTags.push({ x: x, y: y, text: text, maxWidth: maxWidth });
    }

    function flushTags() {
        for (var i = 0; i < pendingTags.length; i++) {
            paintTag(pendingTags[i]);
        }

        pendingTags = [];
    }

    function paintTag(tag) {
        ctx.font = '10px Consolas, "Lucida Console", "Courier New", monospace';

        var width = ctx.measureText ? ctx.measureText(tag.text).width + 8 : tag.text.length * 6 + 8;

        if (width > tag.maxWidth) return;

        var top = placeTag(tag.x, tag.y, width);

        placedTags.push({ x: tag.x, y: top, w: width });

        ctx.fillStyle = 'rgba(0,0,0,0.62)';
        ctx.fillRect(tag.x, top, width, 14);
        ctx.fillStyle = '#E6ECF7';
        ctx.fillText(tag.text, tag.x + 4, top + 10);
    }

    function overlapsTag(x, y, width) {
        for (var i = 0; i < placedTags.length; i++) {
            var tag = placedTags[i];

            if (y < tag.y + 15 && y + 15 > tag.y && x < tag.x + tag.w && x + width > tag.x) {
                return true;
            }
        }

        return false;
    }

    // A perspective floor and a horizon, which is enough to read "camera
    // looking at a space" without pretending to be a renderer.
    function drawFloorGrid(r) {
        var width = r.x2 - r.x1;
        var height = r.y2 - r.y1;
        var vpx = (r.x1 + r.x2) / 2;
        var vpy = r.y1 + height * 0.40;
        var i;
        var t;

        ctx.strokeStyle = 'rgba(80,120,190,0.34)';
        ctx.lineWidth = 1;

        for (i = 1; i <= 7; i++) {
            t = i / 8;
            var y = vpy + (r.y2 - vpy) * (t * t);

            ctx.beginPath();
            ctx.moveTo(r.x1, y);
            ctx.lineTo(r.x2, y);
            ctx.stroke();
        }

        for (i = -7; i <= 7; i++) {
            var baseX = vpx + i * (width / 14);

            ctx.beginPath();
            ctx.moveTo(vpx, vpy);
            ctx.lineTo(baseX, r.y2);
            ctx.stroke();
        }

        ctx.strokeStyle = 'rgba(80,120,190,0.55)';
        ctx.beginPath();
        ctx.moveTo(r.x1, vpy);
        ctx.lineTo(r.x2, vpy);
        ctx.stroke();
    }

    function drawWireCube(r) {
        var width = r.x2 - r.x1;
        var height = r.y2 - r.y1;
        var cx = (r.x1 + r.x2) / 2;
        var cy = r.y1 + height * 0.56;
        var size = Math.min(width, height) * 0.20;
        var angle = 0.6 + frame * 0.0035;
        var cos = Math.cos(angle);
        var sin = Math.sin(angle);
        var corners = [
            [-1, -1, -1], [1, -1, -1], [1, 1, -1], [-1, 1, -1],
            [-1, -1, 1], [1, -1, 1], [1, 1, 1], [-1, 1, 1]
        ];
        var edges = [
            [0, 1], [1, 2], [2, 3], [3, 0],
            [4, 5], [5, 6], [6, 7], [7, 4],
            [0, 4], [1, 5], [2, 6], [3, 7]
        ];
        var points = [];
        var i;

        for (i = 0; i < 8; i++) {
            var p = corners[i];
            var x = p[0] * cos - p[2] * sin;
            var z = p[0] * sin + p[2] * cos;
            var y = p[1] * 0.94 - z * 0.30;
            var zz = p[1] * 0.30 + z * 0.94;
            var perspective = 3.1 / (zz + 4.2);

            points.push([cx + x * perspective * size, cy - y * perspective * size]);
        }

        ctx.strokeStyle = 'rgba(160,190,255,0.85)';
        ctx.lineWidth = 1;

        for (i = 0; i < edges.length; i++) {
            ctx.beginPath();
            ctx.moveTo(points[edges[i][0]][0], points[edges[i][0]][1]);
            ctx.lineTo(points[edges[i][1]][0], points[edges[i][1]][1]);
            ctx.stroke();
        }
    }

    function drawShakeCorners(r, f) {
        // Deterministic wobble: the same frame always draws the same shape,
        // matching the runtime's seeded shake.
        var amplitude = 4 + (f % 7) * 1.5;
        var phase = (f % 11) / 11;
        var dx = Math.sin(phase * 6.28318) * amplitude;
        var dy = Math.cos(phase * 4.71238) * amplitude * 0.6;

        ctx.beginPath();
        ctx.moveTo(r.x1, r.y1 + 14);
        ctx.lineTo(r.x1, r.y1);
        ctx.lineTo(r.x1 + 14, r.y1);

        ctx.moveTo(r.x2 - 14, r.y1);
        ctx.lineTo(r.x2, r.y1);
        ctx.lineTo(r.x2, r.y1 + 14);

        ctx.moveTo(r.x1, r.y2 - 14);
        ctx.lineTo(r.x1, r.y2);
        ctx.lineTo(r.x1 + 14, r.y2);

        ctx.moveTo(r.x2 - 14, r.y2);
        ctx.lineTo(r.x2, r.y2);
        ctx.lineTo(r.x2, r.y2 - 14);
        ctx.stroke();

        ctx.beginPath();
        ctx.moveTo(r.x1 + 24, r.y1 + 24);
        ctx.lineTo(r.x1 + 24 + dx, r.y1 + 24 + dy);
        ctx.stroke();
    }

    function drawParticles(r, seed, f, colour, count) {
        var width = r.x2 - r.x1;
        var height = r.y2 - r.y1;
        var state = ((seed | 0) ^ ((f + 1) * 2654435761)) | 0;
        var total = count || 48;
        var i;

        if (state === 0) state = 1;

        for (i = 0; i < total; i++) {
            // xorshift32, the same generator the emitter uses.
            state ^= state << 13;
            state ^= state >>> 17;
            state ^= state << 5;

            var rx = ((state >>> 0) % 1000) / 1000;
            var ry = ((state >>> 10) % 1000) / 1000;

            // Bias upward and fade with height: sparks, not dust.
            ry = 1 - Math.sqrt(ry);

            var px = r.x1 + rx * width;
            var py = r.y1 + ry * height;
            var radius = 0.6 + (1 - ry) * 1.6;

            ctx.fillStyle = colour || ('rgba(240,200,80,' + (0.25 + (1 - ry) * 0.6) + ')');
            ctx.beginPath();

            if (ctx.arc) {
                ctx.arc(px, py, radius, 0, 6.28318, false);
                ctx.fill();
            } else {
                ctx.fillRect(px, py, radius * 2, radius * 2);
            }
        }
    }

    // Draws the sparks of every emitter in the scene, using the emitter's
    // own start/end colours so a project that recolours its sparks shows it.
    function drawSceneParticles(r, scene, effect, width) {
        var emitterCount = 0;
        var i;

        for (i = 0; i < scene.nodes.length; i++) {
            if (scene.nodes[i].emitter) emitterCount++;
        }

        if (!emitterCount) return;

        var index = 0;

        for (i = 0; i < scene.nodes.length; i++) {
            var node = scene.nodes[i];

            if (!node.emitter) continue;

            var progress = effectProgress(effect, frame);
            var colour = lerpColour(
                str(node.emitter.startColour) || '#96DCFFFF',
                str(node.emitter.endColour) || '#005AFF00',
                progress * 0.35);

            // One emitter is drawn larger than many, so a scene with several
            // does not turn into a solid field of dots.
            drawParticles(r, (intOr(node.emitter.seed, 12345) + index * 7919),
                frame, colourCssFromRgba(colour, emitterCount > 1 ? 0.6 : 0.95),
                emitterCount > 1 ? 24 : 48);

            index++;
        }
    }

    // ================================================================ status

    function setStatus(text, kind) {
        setText(dom.status, text);

        dom.status.className = (kind === 'err') ? 'status-err' : (kind === 'ok' ? 'status-ok' : '');

        if (kind === 'err') toast(text, true);
    }

    function fail(message) {
        setStatus(message, 'err');
    }

    function toast(message, isError) {
        if (!dom.toast) return;

        setText(dom.toast, message);
        dom.toast.className = 'toast show' + (isError ? ' err' : '');

        if (toastTimer) window.clearTimeout(toastTimer);

        toastTimer = window.setTimeout(function () {
            dom.toast.className = 'toast';
        }, isError ? 6000 : 2600);
    }

    function updateStatusRight() {
        setText(dom.statusRight,
            plural(project.scenes.length, 'scene') + ' \u00B7 ' +
            plural(project.timeline.length, 'effect') + ' \u00B7 ' +
            (dirty ? 'modified' : 'saved'));
    }

    function plural(count, singular) {
        return count + ' ' + singular + (count === 1 ? '' : 's');
    }

    // ================================================================ playback

    function setFrame(value) {
        var next = clamp(Math.round(numOr(value, 0)), 0, project.duration);

        frame = next;

        if (!playing) playPos = next;

        updatePlayhead();
        renderPreview();
    }

    function step(delta) {
        pause();
        setFrame(frame + delta);
    }

    function play() {
        if (playing) return;

        playing = true;

        if (frame >= project.duration) frame = 0;

        playPos = frame;
        lastTick = now();

        updateTransport();
        scheduleTick();
    }

    function pause() {
        if (!playing) return;

        playing = false;

        // Exactly one of the two handles is live: the rAF path never sets
        // tickHandle and the timeout path never sets rafId.
        if (rafId !== null && cancelRaf) cancelRaf(rafId);
        else if (tickHandle !== null) window.clearTimeout(tickHandle);

        rafId = null;
        tickHandle = null;

        updateTransport();
    }

    function stop() {
        pause();
        setFrame(0);
    }

    var rafId = null;

    var requestFrame = window.requestAnimationFrame ||
        window.msRequestAnimationFrame ||
        window.webkitRequestAnimationFrame ||
        null;

    var cancelRaf = window.cancelAnimationFrame ||
        window.msCancelAnimationFrame ||
        window.webkitCancelAnimationFrame ||
        null;

    function scheduleTick() {
        if (!playing) return;

        if (requestFrame) {
            rafId = requestFrame(tick);
        } else {
            // MSHTML on XP has no rAF; 16 ms is close enough to 60 Hz.
            tickHandle = window.setTimeout(function () { tick(now()); }, 16);
        }
    }

    function tick() {
        if (!playing) return;

        var time = now();
        var delta = (time - lastTick) / 1000;

        lastTick = time;

        // A stalled or backgrounded tab must not teleport the playhead.
        if (delta > 0.25) delta = 0.25;

        playPos += delta * project.fps;

        if (playPos >= project.duration) {
            playing = false;
            setFrame(project.duration);
            updateTransport();
            return;
        }

        setFrame(playPos);
        scheduleTick();
    }

    function updateTransport() {
        if (!dom.btnPlay) return;

        dom.btnPlay.className = 'tbtn' + (playing ? ' on' : '');
        setText(dom.btnPlay, playing ? '\u2016' : '\u25B6');
        dom.btnPlay.title = playing ? 'Pause (Space)' : 'Play (Space)';
    }

    // ================================================================ files

    function saveProject() {
        var text = serializeProject();
        var name = safeFileName(project.name);

        if (hostAlive) {
            hostCall('save', text);
            setStatus('Handed the project to the host.');
            return;
        }

        if (downloadText(text, name)) {
            dirty = false;
            updateStatusRight();
            setStatus('Downloaded ' + name + '.', 'ok');
            toast('Saved ' + name);
            return;
        }

        // No host and no Blob: show the JSON so it can be copied out by hand.
        showModal('Project JSON \u2014 copy and save as ' + name, text);
        setStatus('This browser cannot download files; the JSON is in the dialog.', 'err');
    }

    function openProject() {
        if (hostAlive) {
            hostCall('open', '');
            setStatus('Asked the host to open a project.');
            return;
        }

        if (!dom.fileInput || !window.FileReader) {
            fail('This browser cannot open local files. Use "Download" and edit the JSON.');
            return;
        }

        dom.fileInput.click();
    }

    function downloadProject() {
        var text = serializeProject();
        var name = safeFileName(project.name);

        if (downloadText(text, name)) {
            setStatus('Downloaded ' + name + '.', 'ok');
            return;
        }

        showModal('Project JSON \u2014 copy and save as ' + name, text);
        setStatus('This browser cannot download files; the JSON is in the dialog.', 'err');
    }

    function downloadText(text, name) {
        var nav = window.navigator || {};

        // IE10/11: the only supported way to save a blob.
        if (nav.msSaveBlob && window.Blob) {
            nav.msSaveBlob(new Blob([text], { type: 'application/json' }), name);
            return true;
        }

        if (!window.Blob || !window.URL || !window.URL.createObjectURL) return false;

        var blob = new Blob([text], { type: 'application/json' });
        var url = window.URL.createObjectURL(blob);
        var link = document.createElement('a');

        link.href = url;
        link.download = name;
        link.style.display = 'none';

        document.body.appendChild(link);
        link.click();
        document.body.removeChild(link);

        window.setTimeout(function () {
            window.URL.revokeObjectURL(url);
        }, 4000);

        return true;
    }

    function loadText(text, sourceName) {
        var parsed;

        try {
            parsed = JSON.parse(text);
        } catch (error) {
            fail('That file is not valid JSON: ' + error.message);
            return false;
        }

        if (!isObject(parsed)) {
            fail('That file is not a project: the root value is not an object.');
            return false;
        }

        applyProject(parsed);
        setStatus('Loaded "' + project.name + '"' + (sourceName ? ' from ' + sourceName : '') + '.', 'ok');
        return true;
    }

    function applyProject(raw) {
        // Stop any playback first, so a scheduled frame cannot paint over
        // the project that is about to replace it.
        pause();

        project = normalizeProject(raw);

        frame = 0;
        playPos = 0;
        playing = false;
        dirty = false;
        collapsed = {};
        selection = { kind: 'project' };

        syncTopbar();
        layout();
        renderTree();
        renderInspector();
        updateTransport();
        updateStatusRight();
    }

    // ================================================================ top bar

    function syncTopbar() {
        setInputValue(dom.projName, project.name);
        setInputValue(dom.projW, project.width);
        setInputValue(dom.projH, project.height);
        setInputValue(dom.projFps, project.fps);
        setInputValue(dom.projDur, project.duration);

        setText(dom.frameTotal, String(project.duration));
        setText(dom.tlTotal, String(project.duration));
        setText(dom.frameNow, pad(frame, 4));

        if (dom.themeSel) dom.themeSel.value = theme;
    }

    function setInputValue(input, value) {
        if (!input) return;

        // Never fight the caret: an inspector edit can reach here while the
        // same value is being typed into the top bar.
        if (document.activeElement === input) return;

        input.value = String(value);
    }

    function bindTopbarNumber(input, apply, min, max) {
        if (!input) return;

        on(input, 'change', function () {
            var value = parseInt(input.value, 10);

            if (!isFinite(value)) {
                syncTopbar();
                return;
            }

            value = clamp(value, min, max);
            input.value = String(value);
            apply(value);
        });
    }

    // ================================================================ theme

    function setTheme(name) {
        var next = 'light';

        for (var i = 0; i < THEMES.length; i++) {
            if (THEMES[i] === name) next = name;
        }

        theme = next;
        document.body.className = next;

        if (dom.themeSel) dom.themeSel.value = next;

        applyBrandPalette(next);
        renderPreview();
    }

    // A fork that changes Branding.cs should see the editor change too, so
    // the host may splice a palette. Inline properties on <body> beat the
    // body.light/.dark/.oled rules, and are re-applied on every switch.
    function applyBrandPalette(name) {
        var style = document.body.style;

        if (!style || !style.setProperty) return;

        var palette = BRAND.palette && BRAND.palette[name];
        var keys = ['primary', 'primary2', 'bg', 'surface', 'text'];
        var vars = ['--brand-primary', '--brand-primary2', '--brand-bg', '--brand-surface', '--brand-text'];

        for (var i = 0; i < vars.length; i++) {
            var value = palette ? palette[keys[i]] : null;

            if (value) style.setProperty(vars[i], value);
            else if (style.removeProperty) style.removeProperty(vars[i]);
        }
    }

    function detectNoVars() {
        var probe = document.createElement('div');
        var supported = false;

        try {
            probe.style.setProperty('--dpt-probe', '#123456');
            document.body.appendChild(probe);

            var computed = window.getComputedStyle ? window.getComputedStyle(probe, null) : null;

            if (computed) {
                supported = trimStr(computed.getPropertyValue('--dpt-probe')).length > 0;
            }

            document.body.removeChild(probe);
        } catch (error) {
            supported = false;
        }

        if (!supported) {
            document.documentElement.className += ' no-vars';
        }
    }

    // ================================================================ menu / modal

    function buildAddMenu() {
        var menu = dom.addMenu;

        clearNode(menu);
        menu.appendChild(el('div', 'menu-head', 'Effect type'));

        for (var i = 0; i < EFFECTS.length; i++) {
            (function (meta) {
                var item = el('button', 'menu-item');
                var dot = el('span', 'mi-dot');

                item.setAttribute('type', 'button');
                dot.style.backgroundColor = meta.tint;

                item.appendChild(dot);
                item.appendChild(document.createTextNode(meta.label));
                item.appendChild(el('span', 'mi-type', meta.type));

                on(item, 'click', function () {
                    closeMenu();
                    addEffect(meta.type);
                });

                menu.appendChild(item);
            })(EFFECTS[i]);
        }
    }

    function openMenu() {
        dom.addMenu.className = 'menu';
    }

    function closeMenu() {
        dom.addMenu.className = 'menu hidden';
    }

    function toggleMenu() {
        if (dom.addMenu.className.indexOf('hidden') >= 0) openMenu();
        else closeMenu();
    }

    function showModal(title, text) {
        setText(dom.modalTitle, title);
        dom.modalText.value = text;
        dom.modal.className = 'modal';
        dom.modalText.focus();
        dom.modalText.select();
    }

    function closeModal() {
        dom.modal.className = 'modal hidden';
    }

    // ================================================================ layout

    function layout() {
        sizeCanvas();
        renderTimeline();
        renderPreview();
    }

    var resizeTimer = null;

    function scheduleLayout() {
        if (resizeTimer) window.clearTimeout(resizeTimer);

        resizeTimer = window.setTimeout(function () {
            resizeTimer = null;
            layout();
        }, 60);
    }

    // ================================================================ events

    function bindEvents() {
        // ---------------------------------------------------------- transport
        on(dom.btnPlay, 'click', function () {
            if (playing) pause();
            else play();
        });

        on(dom.btnStop, 'click', stop);

        // ---------------------------------------------------------- top bar
        on(dom.projName, 'change', function () {
            project.name = trimStr(dom.projName.value) || 'Untitled';
            dom.projName.value = project.name;
            renderInspector();
            touch();
        });

        bindTopbarNumber(dom.projW, function (value) {
            project.width = value;
            layout();
            renderInspector();
            touch();
        }, 16, 8192);

        bindTopbarNumber(dom.projH, function (value) {
            project.height = value;
            layout();
            renderInspector();
            touch();
        }, 16, 8192);

        bindTopbarNumber(dom.projFps, function (value) {
            project.fps = value;
            renderInspector();
            touch();
        }, 1, 1000);

        bindTopbarNumber(dom.projDur, function (value) {
            setDuration(value);
        }, 1, 1000000);

        on(dom.themeSel, 'change', function () {
            setTheme(dom.themeSel.value);
        });

        on(dom.btnNew, 'click', function () {
            if (dirty && !window.confirm('Discard unsaved changes and start a new project?')) return;

            applyProject(createDefaultProject());
            setStatus('New project.');
        });

        on(dom.btnOpen, 'click', openProject);
        on(dom.btnSave, 'click', saveProject);
        on(dom.btnDownload, 'click', downloadProject);

        // ---------------------------------------------------------- files
        on(dom.fileInput, 'change', function () {
            var file = dom.fileInput.files && dom.fileInput.files[0];

            if (!file) return;

            var reader = new FileReader();

            reader.onload = function () {
                loadText(String(reader.result), file.name);
            };

            reader.onerror = function () {
                fail('Could not read ' + file.name + '.');
            };

            reader.readAsText(file, 'utf-8');
            dom.fileInput.value = '';
        });

        // ---------------------------------------------------------- menu
        on(dom.btnAdd, 'click', function (event) {
            stopEvent(event);
            toggleMenu();
        });

        on(document, 'mousedown', function (event) {
            var e = event || window.event;

            if (dom.addMenu.className.indexOf('hidden') >= 0) return;

            var target = e.target || e.srcElement;

            if (contains(dom.addMenu, target) || target === dom.btnAdd) return;

            closeMenu();
        });

        // ---------------------------------------------------------- modal
        on(dom.modalClose, 'click', closeModal);

        on(dom.modal, 'mousedown', function (event) {
            var e = event || window.event;
            var target = e.target || e.srcElement;

            if (target === dom.modal) closeModal();
        });

        // ---------------------------------------------------------- delete
        on(dom.btnDelete, 'click', deleteSelectedEffect);

        // ---------------------------------------------------------- timeline
        on(dom.tlRuler, 'mousedown', function (event) {
            beginDrag('scrub', null, event || window.event);
        });

        // Clicking the empty strip below the last track scrubs too; only the
        // container itself, since clicks on a lane are handled per lane.
        on(dom.tlLanes, 'mousedown', function (event) {
            var e = event || window.event;
            var target = e && (e.target || e.srcElement);

            if (target === dom.tlLanes) beginDrag('scrub', null, e);
        });

        on(document, 'mousemove', handleDragMove);
        on(document, 'mouseup', endDrag);
        on(window, 'blur', endDrag);

        // ---------------------------------------------------------- resize
        on(window, 'resize', scheduleLayout);

        // ---------------------------------------------------------- keyboard
        on(document, 'keydown', function (event) {
            var e = event || window.event;

            if (!e) return;

            var key = e.keyCode || e.which;
            var ctrl = e.ctrlKey || e.metaKey;
            var target = e.target || e.srcElement;
            var tag = target && target.tagName ? target.tagName.toLowerCase() : '';
            var editing = (tag === 'input' || tag === 'textarea' || tag === 'select');

            if (ctrl && key === 83) {
                stopEvent(e);
                saveProject();
                return;
            }

            if (key === 27) {
                closeMenu();
                if (dom.modal.className.indexOf('hidden') < 0) closeModal();
                return;
            }

            if (editing) return;

            if (key === 32) {
                stopEvent(e);
                if (playing) pause();
                else play();
                return;
            }

            if (key === 37) {
                stopEvent(e);
                step(e.shiftKey ? -10 : -1);
                return;
            }

            if (key === 39) {
                stopEvent(e);
                step(e.shiftKey ? 10 : 1);
                return;
            }

            if (key === 46) {
                stopEvent(e);
                deleteSelectedEffect();
            }
        });
    }

    // ================================================================ boot

    function cacheDom() {
        dom.app = $('app');
        dom.brandName = $('brandName');
        dom.projName = $('projName');
        dom.projW = $('projW');
        dom.projH = $('projH');
        dom.projFps = $('projFps');
        dom.projDur = $('projDur');
        dom.btnPlay = $('btnPlay');
        dom.btnStop = $('btnStop');
        dom.frameNow = $('frameNow');
        dom.frameTotal = $('frameTotal');
        dom.themeSel = $('themeSel');
        dom.btnNew = $('btnNew');
        dom.btnOpen = $('btnOpen');
        dom.btnSave = $('btnSave');
        dom.btnDownload = $('btnDownload');

        dom.tree = $('tree');
        dom.preview = $('preview');
        dom.previewFrame = $('previewFrame');
        dom.previewNote = $('previewNote');
        dom.hudLeft = $('hudLeft');
        dom.hudRight = $('hudRight');

        dom.btnAdd = $('btnAdd');
        dom.addMenu = $('addMenu');
        dom.tlTitle = $('tlTitle');
        dom.tlFrame = $('tlFrame');
        dom.tlTotal = $('tlTotal');
        dom.tlRuler = $('tlRuler');
        dom.tlRulerWrap = dom.tlRuler.parentNode;
        dom.tlScroll = $('tlScroll');
        dom.tlGutter = $('tlGutter');
        dom.tlLanes = $('tlLanes');
        dom.tlPlayhead = $('tlPlayhead');

        dom.inspector = $('inspector');
        dom.inspectorTitle = $('inspectorTitle');
        dom.btnDelete = $('btnDelete');

        dom.status = $('status');
        dom.statusRight = $('statusRight');
        dom.toast = $('toast');

        dom.fileInput = $('fileInput');
        dom.modal = $('modal');
        dom.modalTitle = $('modalTitle');
        dom.modalText = $('modalText');
        dom.modalClose = $('modalClose');
    }

    function detectCapabilities() {
        var probe = document.createElement('input');

        try {
            probe.setAttribute('type', 'color');
            supportsColourInput = (probe.type === 'color');
        } catch (error) {
            supportsColourInput = false;
        }
    }

    function boot() {
        if (booted) return;

        cacheDom();
        detectNoVars();
        detectCapabilities();

        document.title = BRAND_TITLE + ' \u2014 Editor';

        if (dom.brandName) setText(dom.brandName, BRAND_NAME);
        if (dom.brandSub) {
            // The tagline is the Branding.cs one-liner; the panel is narrow,
            // so only the version is shown there.
            setText(dom.brandSub, 'EDITOR \u00B7 v' + BRAND_VERSION);
            dom.brandSub.title = BRAND_TAGLINE;
        }

        if (!window.JSON) {
            setStatus('This browser has no JSON support; the editor cannot load or save projects.', 'err');
        }

        if (!dom.preview || !dom.preview.getContext) {
            setText(dom.previewNote, 'This browser cannot draw the preview (no canvas support).');
        }

        setTheme(theme);

        project = createDefaultProject();

        bindEvents();
        buildAddMenu();
        applyProject(project);

        booted = true;

        flushHostQueue();

        // Announce ourselves after the first paint, so a host that replies
        // immediately is talking to a page that is already interactive.
        //
        // Only in an embedded engine. Navigating to an unregistered scheme
        // from a normal browser turns into a console error at best and a
        // "cannot open" page at worst, and the bridge is only meaningful
        // when something is listening on the other end. documentMode is the
        // reliable MSHTML tell; DPT_HOST lets a non-MSHTML host opt in by
        // setting the flag before this script runs.
        window.setTimeout(function () {
            if (window.DPT_HOST === true || typeof document.documentMode === 'number') {
                hostCall('ready', '');
            }
        }, 0);
    }

    // ------------------------------------------------------------ public api
    //
    // Small surface for the host and for poking the editor from a console.
    // Not part of the file format and not required by the bridge.

    window.dptengineEditor = {
        load: function (text) { return loadText(String(text), 'api'); },
        save: function () { return serializeProject(); },
        project: function () { return project; },
        setFrame: function (value) { setFrame(value); },
        frame: function () { return frame; },
        setTheme: function (name) { setTheme(name); },
        // The same helper the page uses for host->page navigation, exposed so
        // a host-injected script can raise a verb without duplicating the
        // payload handshake.
        hostCall: function (verb, payload) { hostCall(verb, payload); },
        version: BRAND_VERSION
    };

    // app.js is spliced at the end of the body, so the document is parsed
    // already; the readyState check keeps the file usable from a head
    // include or an async loader without a second code path.
    if (document.readyState === 'complete' || document.readyState === 'interactive') {
        boot();
    } else {
        on(document, 'DOMContentLoaded', boot);
        on(window, 'load', boot);
    }
})();