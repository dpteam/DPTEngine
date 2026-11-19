# Project format (.dptproj)

A project is one UTF-8 JSON file. Everything it needs is inside it, including textures, so a
demo is a single file you can copy, mail or publish. The file is written by
`ProjectSerializer.ToText` and read by `ProjectSerializer.LoadFromText`; every field below is
taken from those two functions, including the defaults used when a field is missing.

Parsing is strict: no comments, no trailing commas, no unquoted keys. Member order is preserved
on save so a project diffs cleanly in git. Numbers are doubles internally, which is exact for
every integer a project can contain.

After loading, `ProjectFile.Normalize` repairs the document; see [Normalisation](#normalisation).

## Reading the original Addict2 format

The engine also reads the `.dmo` files the original editor produced, so existing work does not
have to be redone. `ProjectSerializer.LoadAny` sniffs the first eight bytes and picks the
reader, which means a renamed file still opens: the `.dmo` signature is the ASCII text
`aDDict2P`.

The format is a chunk stream - an eight byte signature, then a sequence of
`[4 byte id][int32 size][payload]` records, terminated by `END0`. Every record carries its own
size, and `DmoReader` checks after each one that it consumed exactly that many bytes. A wrong
struct size therefore fails on the record that is wrong, with the byte counts, instead of
corrupting the next nineteen. That self-check is what makes the format practical to port: the
record sizes were confirmed against all nineteen projects that ship with the original sources.

What the importer does with each part of the file:

| Addict2 | Becomes |
| --- | --- |
| `TEX0`/`TEX1` texture recipes | Pixels, by running the recipe through the texture generator (37 filters) |
| `SCN0`/`SCN1` mesh scenes | A merged mesh, with each object's matrix baked in |
| `WRL0`-`WRL2` worlds | A `Scene`, with camera, fog and lighting flags |
| `WRO0`-`WRO5` world objects | Scene nodes, including emitters and scene references |
| `CAM0` camera splines | `cameraTracks` on the scene |
| `WRA0`/`WOA0`-`WOA2` animations | Camera and object tracks |
| `EVT0`-`EVT2` timeline events | Timeline effects |
| `OBJ0`-`OBJ5` objects | Generated primitives or stored geometry, then baked into the mesh |

Two details are worth knowing because they change what you see:

* **Blend factors are stored the other way round.** The original calls
  `glBlendFunc(DSTBlend, SRCBlend)`, so the field named `DSTBlend` is the OpenGL *source*
  factor. The importer swaps them back. A stored pair of `0/0` is the editor's "never set"
  default and becomes opaque, because taking `GL_ZERO` literally would draw nothing - which
  is how eight of the nine objects in the tutorial project are stored.
* **Object matrices are not transposed.** The original stores a row-major `float[4][4]` and
  hands the same floats to `glMultMatrixf`, which reads column-major, so OpenGL uses the
  transpose of the array as written. Working the two conventions through, the engine's
  row-vector matrix is the row-major reading of the bytes, unchanged.

Anything the importer does not model is recorded rather than dropped silently: map-transform
modifiers, mesh smoothing and clones are kept as nodes with their type set, and deflectors and
gravity wells become empty parent nodes that still pass their transform to their children.

## Top level

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `format` | int | `1` | Format version. Current writer always emits `1` |
| `name` | string | `"Untitled"` | Project name, shown in logs and the overlay |
| `author` | string | `""` | Free text |
| `width` | int | `1024` | Render width of the project buffer |
| `height` | int | `768` | Render height of the project buffer |
| `fps` | int | `100` | Timeline frames per second |
| `duration` | int | `600` | Timeline length in frames |
| `textures` | array | `[]` | See [Textures](#textures) |
| `scenes` | array | `[]` | See [Scenes](#scenes) |
| `timeline` | array | `[]` | See [Timeline effects](#timeline-effects) |

`width` and `height` are the resolution the whole project is authored against, not the window
size. Playback renders into a buffer of that size and letterboxes it to the window.

## Textures

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `id` | int | `0` | Identifier referenced by materials and effects |
| `width` | int | `0` | Pixel width |
| `height` | int | `0` | Pixel height |
| `pixels` | string | `""` | Base64 of the raw pixels |

`pixels` is the raw `uint32` ARGB words, four bytes per pixel, row-major from the top-left
corner. On a little-endian machine the byte order inside each pixel is **B, G, R, A** - the
words are copied straight out of a `uint[]` with `Buffer.BlockCopy`. The decoded length must be
at least `width * height * 4`; a texture with no data, or with unreadable base64, is dropped
with a warning.

There is no PNG or DDS path: a deflate implementation and a second decode path were not worth
it, and the browser-based editor can produce raw pixels directly. A 512x512 texture is about a
megabyte of base64.

## Scenes

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `id` | int | `0` | Identifier referenced by `display3d` and `particles` |
| `name` | string | `"scene"` | Free text |
| `camera` | object | see below | The scene's camera |
| `fog` | object | see below | Linear fog |
| `ambient` | string | `"#FFFFFFFF"` | Ambient light colour |
| `lightDirection` | array[3] | `[0.4, -0.7, 0.6]` | World-space direction of the key light |
| `nodes` | array | `[]` | Scene nodes |
| `animations` | array | `[]` | Named animations |
| `cameraTracks` | array | `[]` | Camera animation |

### Camera

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `position` | array[3] | `[0, 0, -10]` | Eye position |
| `target` | array[3] | `[0, 0, 0]` | Look-at point |
| `up` | array[3] | `[0, 1, 0]` | Up vector |
| `fov` | float | `45` | Vertical field of view, degrees, clamped to 1..179 |
| `roll` | float | `0` | Rotation around the view axis, degrees |
| `near` | float | `0.1` | Near plane |
| `far` | float | `2000` | Far plane |

### Fog

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `enabled` | bool | `false` | Fog on or off |
| `colour` | string | `"#000000"` | Fog colour |
| `start` | float | `0` | View-space distance where fog begins |
| `end` | float | `100` | Distance where fog is fully applied |

Fog is computed in the pixel shader from view-space depth. A zero-width range is clamped to
0.0001 so the division cannot produce NaN.

### Nodes

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `id` | int | `0` | Node identifier, unique within the scene |
| `parent` | int | `-1` | Parent node id, `-1` for a root |
| `name` | string | `""` | Free text, used in log messages |
| `visible` | bool | `true` | Hidden nodes are skipped by the renderer |
| `position` | array[3] | `[0, 0, 0]` | Local translation |
| `rotation` | array[4] | `[0, 0, 0, 1]` | Local rotation as a quaternion, `x, y, z, w`. Normalised on load |
| `scale` | array[3] | `[1, 1, 1]` | Local scale |
| `mesh` | object | absent | See [Meshes](#meshes). Absent means the node draws nothing |
| `material` | object | defaults | See [Materials](#materials). Always present after a load |
| `emitter` | object | absent | See [Emitters](#emitters) |

The local matrix is `Scale * Rotation * Translation`, in that order, and children are ids so a
file may list a child before its parent. A `parent` that does not exist is cleared to `-1` and
the node becomes a root, rather than being dropped.

### Meshes

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `primitive` | string | `"box"` | `box`, `sphere`, `plane` or `custom` |
| `size` | float | `2` | Box edge length; sphere diameter; plane width and depth |
| `segments` | int | `16` | Sphere longitude segments; plane divisions are `segments / 4` |
| `rings` | int | `8` | Sphere latitude rings |
| `vertices` | array | only for `custom` | See below |
| `indices` | array | only for `custom` | Triangle indices into `vertices` |

Procedural meshes are stored as their recipe, not as baked geometry: a sphere is a few hundred
bytes as parameters and a few hundred kilobytes as vertices, and the editor can still change the
segment count. On load, `sphere` builds `CreateSphere(size / 2, segments, rings)`, `plane` builds
`CreatePlane(size, size, max(1, segments / 4))`, and anything unrecognised builds a box.

A `custom` mesh stores each vertex as an eight-number array:

```
[px, py, pz, nx, ny, nz, u, v]
```

Entries with fewer than eight numbers are skipped. `indices` is a flat list of triangle indices,
three per triangle. Bounds are recalculated after loading; normals are not, so they must be
supplied.

### Materials

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `texture` | int | `-1` | Texture id for the base map, `-1` for none |
| `environment` | int | `-1` | Texture id used as a sphere environment map, `-1` for none |
| `colour` | string | `"#FFFFFFFF"` | Modulation colour |
| `sourceBlend` | string | `"sourceAlpha"` | Source blend factor |
| `destinationBlend` | string | `"inverseSourceAlpha"` | Destination blend factor |
| `shading` | string | `"gouraud"` | `unlit`, `flat` or `gouraud` |
| `wireframe` | bool | `false` | Draw as lines |
| `twoSided` | bool | `false` | Disable back-face culling |
| `depthWrite` | bool | `true` | Write the depth buffer |
| `depthTest` | bool | `true` | Test against the depth buffer |
| `transparent` | bool | `false` | Draw in the sorted transparent pass |

Blend names, as written by `Effect.BlendName` and accepted by `Effect.ParseBlend`:

`zero`, `one`, `sourceColour`, `inverseSourceColour`, `sourceAlpha`, `inverseSourceAlpha`,
`destinationAlpha`, `inverseDestinationAlpha`, `destinationColour`, `inverseDestinationColour`,
`sourceAlphaSaturation`.

The parser also accepts the American spelling (`sourceColor` and so on). An unknown name falls
back to the default for that field rather than failing the load.

Note that `shading` is stored and round-tripped but not used yet: the renderer disables lighting
globally and chooses its pixel shader from the presence of a texture or environment map, so
`unlit`, `flat` and `gouraud` all draw the same today. The field is reserved for a lit path.

### Emitters

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `shape` | string | `"point"` | `point`, `sphere`, `box` or `disc` |
| `maxParticles` | int | `512` | Pool size |
| `rate` | float | `8` | Particles per second |
| `lifetime` | float | `1.5` | Seconds |
| `lifetimeChaos` | float | `0.5` | Random variation added to the lifetime |
| `speed` | float | `3` | Initial speed, units per second |
| `speedChaos` | float | `1` | Random variation added to the speed |
| `direction` | array[3] | `[0, 1, 0]` | Emission direction |
| `spread` | float | `25` | Cone half-angle, degrees |
| `startSize` | float | `0.5` | Size at birth |
| `endSize` | float | `0.05` | Size at death |
| `sizeChaos` | float | `0.3` | Random size variation |
| `startColour` | string | `"#FFC850FF"` | Colour at birth |
| `endColour` | string | `"#FF280000"` | Colour at death |
| `gravity` | array[3] | `[0, -4, 0]` | Constant acceleration |
| `radius` | float | `0.5` | Sphere and disc emission radius |
| `boxSize` | array[3] | `[0.5, 0.5, 0.5]` | Box emission volume |
| `spin` | float | `45` | Degrees per second |
| `seed` | int | `12345` | Random seed, so a demo looks the same every run |

Particles simulate on a fixed 100 Hz tick, the same cadence as the timeline.

### Animations and tracks

```json
"animations": [
  { "id": 0, "name": "idle", "tracks": [ ... ] }
]
```

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `id` | int | `0` | Animation id, referenced by `display3d` and `particles` |
| `name` | string | `""` | Free text |
| `tracks` | array | `[]` | One track per animated property |

A track:

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `property` | string | required | Property name, see below. A track without one is skipped |
| `interpolation` | string | `"linear"` | `step`, `linear`, `hermite`, `catmull`, `bezier`, `sine`, `saw`, `square` |
| `keys` | array | `[]` | `[time, value]` pairs, sorted by time on load |

Key times are in **seconds**, not frames. Sampling outside the key range clamps to the first or
last value; it never extrapolates.

Node properties are `nodeId.suffix`:

| Suffix | Property |
| --- | --- |
| `x`, `y`, `z` | Position component |
| `sx`, `sy`, `sz` | Scale component |
| `rx`, `ry`, `rz`, `rw` | Rotation quaternion component, renormalised after every sample |
| `r`, `g`, `b`, `a` | Material colour channel, 0..255 |

Camera tracks use bare names, because there is one camera per scene:

| Property | Property |
| --- | --- |
| `eyeX`, `eyeY`, `eyeZ` | Camera position |
| `targetX`, `targetY`, `targetZ` | Camera target |
| `fov` | Field of view, clamped to 1..179 |
| `roll` | Roll, degrees |

The `loop` flag on a spline is **not serialised**. A curve that should loop is authored with its
last key equal to its first, which is what the built-in template does.

## Timeline effects

Every effect shares these fields, written first:

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `type` | string | required | Effect type, see below. An unknown type is skipped with a warning |
| `start` | int | `0` | First frame the effect is active |
| `end` | int | `0` | Last frame, inclusive |
| `rect` | array[4] | `[0, 0, 800, 600]` | Rectangle at `start`, as `[x, y, width, height]` |
| `rectEnd` | array[4] | same as `rect` | Rectangle at `end`, interpolated linearly across the span |

The rectangle lives in a virtual 800x600 space and is scaled to the project resolution, so a
project plays the same at any size. `width` and `height` are extents, not right and bottom edges.
A rectangle that interpolates to zero or negative size is clamped to one pixel, because an
invalid viewport makes the driver reject the draw.

The timeline is sorted by `start` after loading. The sort is not stable, so do not rely on the
order of two effects that begin on the same frame; author explicit ordering by giving them
different start frames, or by relying on the effect type (a `clear` at frame 0 and a `display3d`
at frame 0 work because the clear draws first only by luck of the sort).

### `clear`

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `colour` | string | `"#000000"` | Clear colour |
| `screen` | bool | `true` | Clear the colour buffer |
| `depth` | bool | `true` | Clear the depth buffer |

### `layer2d`

Draws a coloured or textured rectangle into the frame.

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `startColour` | string | `"#FFFFFFFF"` | Colour at `start` |
| `endColour` | string | `"#FFFFFFFF"` | Colour at `end` |
| `sourceBlend` | string | `"sourceAlpha"` | Source blend factor |
| `destinationBlend` | string | `"inverseSourceAlpha"` | Destination blend factor |
| `texture` | int | `-1` | Texture id, `-1` for a flat colour |

### `display3d`

Renders a scene through a camera into the effect rectangle.

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `scene` | int | `0` | Scene id. A missing scene logs a warning and draws nothing |
| `camera` | int | `0` | Stored but not used; a scene has one camera |
| `animation` | int | `-1` | Scene animation id, `-1` for none |
| `cameraStart` | float | `0` | Camera track time at `start`, seconds |
| `cameraEnd` | float | `1` | Camera track time at `end`, seconds |
| `animationStart` | float | `0` | Animation time at `start`, seconds |
| `animationEnd` | float | `1` | Animation time at `end`, seconds |
| `dontSaveCamera` | bool | `false` | When true, pending camera-shake offsets survive this effect |

The aspect ratio comes from the rectangle, not the window, so a 3D view squeezed into a wide band
does not stretch.

### `rendertotexture`

Captures the current frame buffer into a slot.

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `slot` | int | `0` | Slot index, 0..5 |

Slots are half the project resolution and shared between effects.

### `blur`

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `startColour` | string | `"#CCFFFFFF"` | Tint at `start` |
| `endColour` | string | `"#CCFFFFFF"` | Tint at `end` |
| `sourceBlend` | string | `"one"` | Source blend factor |
| `destinationBlend` | string | `"one"` | Destination blend factor |
| `slot` | int | `0` | Slot to capture into and sample from |
| `layers` | int | `4` | Number of successive passes |
| `zoom` | int | `0` | Per-layer offset, 0..255, scaled inside the shader |

Each layer blurs the previous layer's result and composites it back, so cost is linear in
`layers` while the visual result is a wide, smooth blur.

### `feedback`

The same fields as `blur`, with different defaults and a different shader: it samples the previous
frame slightly scaled around the centre, which makes the image smear outward and leaves trails
behind moving objects.

| Field | Type | Default |
| --- | --- | --- |
| `startColour` | string | `"#E6FFFFFF"` |
| `endColour` | string | `"#E6FFFFFF"` |
| `sourceBlend` | string | `"one"` |
| `destinationBlend` | string | `"one"` |
| `slot` | int | `0` |
| `layers` | int | `6` |
| `zoom` | int | `8` |

### `contrast`

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `startColour` | string | `"#FFFFFFFF"` | Tint at `start` |
| `endColour` | string | `"#FFFFFFFF"` | Tint at `end` |
| `sourceBlend` | string | `"sourceAlpha"` | Source blend factor |
| `destinationBlend` | string | `"inverseSourceAlpha"` | Destination blend factor |
| `slot` | int | `0` | Slot to sample |
| `multiply` | int | `0` | Contrast multiplier, 0 means unchanged |
| `add` | int | `0` | Brightness offset, 0 means unchanged |

Both values are biased so that zero is neutral: the shader receives `1 + multiply / 64` and
`1 + add / 255`.

### `camerashake`

Does not draw. Leaves an offset for the following `display3d` effects.

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `eye` | float | `0.1` | Eye offset scale, world units |
| `target` | float | `0.1` | Target offset scale |
| `shakeStart` | float | `1` | Strength at `start` |
| `shakeEnd` | float | `1` | Strength at `end` |
| `frequency` | int | `5` | Offset changes per second times ten; minimum 1 |

The offset is recomputed from a seeded generator at a fixed frequency and interpolated between
two ticks, so it is deterministic per frame and scrubbing the timeline does not change it.

### `particles`

Does not draw. Advances a scene's emitters for the current frame, so the `display3d` after it
shows the result.

| Field | Type | Default | Meaning |
| --- | --- | --- | --- |
| `scene` | int | `0` | Scene id |
| `animation` | int | `0` | Scene animation id, `-1` for none |
| `animationStart` | float | `0` | Animation time at `start`, seconds |
| `animationEnd` | float | `1` | Animation time at `end`, seconds |

## Colours

Colours are hex strings. `ColorRgba.Parse` accepts:

* `#RRGGBB` or `RRGGBB` - opaque, alpha becomes 255;
* eight digits, read as `AARRGGBB`;
* anything else - white.

`ColorRgba.ToHex` writes `#RRGGBBAA`.

**These two do not agree, and it is a known bug.** A colour written by the engine is re-read with
its channels rotated: `#3CB4FF` (a blue) is saved as `#3CB4FFFF` and comes back as
`#B4FFFF3C`. Only greys with alpha 255 (`#FFFFFF`, `#000000`) survive a save and reload.

Practical consequences:

* A hand-written project with six-digit colours loads correctly, because the reader is the only
  side involved.
* Anything saved by `ProjectSerializer.Save` and loaded again has different colours, and a
  translucent colour loses its alpha.
* The built-in template is built in code and is not affected until it is saved.

Until the writer is changed to emit `AARRGGBB` (or the reader to accept `RRGGBBAA`), treat the
saved file as the authoritative source and check colours after a save. The example below is
written for the reader, so it loads as intended.

## Normalisation

`ProjectFile.Normalize` runs after every load and repairs what it can, logging each change:

| Condition | Repair |
| --- | --- |
| `width` <= 0 or > 8192 | 1024 |
| `height` <= 0 or > 8192 | 768 |
| `fps` <= 0 or > 1000 | 100 |
| `duration` <= 0 | `fps * 5` |
| Texture dimensions not positive, or pixel array shorter than `width * height` | Texture dropped |
| Texture not a power of two in either axis | Warning only; kept |
| Effect `end` < `start` | Swapped |
| Effect `start` < 0 | 0 |
| Effect `end` > `duration` | `duration` |
| Timeline order | Sorted by `start` |
| Dangling node parent | Parent cleared, node becomes a root |
| Zero-length quaternion | Replaced with identity |

Saving is atomic: the text is written to `<path>.tmp` and then moved over the target, so a crash
during a save cannot truncate the project.

## Complete example

A 2x2 texture, one scene with a box and a plane, one animation, one camera track, and four
effects.

```json
{
  "format": 1,
  "name": "Minimal",
  "author": "",
  "width": 800,
  "height": 600,
  "fps": 100,
  "duration": 500,
  "textures": [
    {
      "id": 1,
      "width": 2,
      "height": 2,
      "pixels": "MCAQ/zAgEP8wIBD/MCAQ/w=="
    }
  ],
  "scenes": [
    {
      "id": 1,
      "name": "main",
      "camera": {
        "position": [0, 1.5, -6],
        "target": [0, 0, 0],
        "up": [0, 1, 0],
        "fov": 50,
        "roll": 0,
        "near": 0.1,
        "far": 1000
      },
      "fog": {
        "enabled": true,
        "colour": "#040810",
        "start": 5,
        "end": 30
      },
      "ambient": "#8096B4",
      "lightDirection": [0.3, -0.5, 0.8],
      "nodes": [
        {
          "id": 1,
          "parent": -1,
          "name": "floor",
          "visible": true,
          "position": [0, -1, 0],
          "rotation": [0, 0, 0, 1],
          "scale": [1, 1, 1],
          "mesh": { "primitive": "plane", "size": 20, "segments": 4, "rings": 8 },
          "material": {
            "texture": 1,
            "environment": -1,
            "colour": "#FFFFFFFF",
            "sourceBlend": "one",
            "destinationBlend": "zero",
            "shading": "unlit",
            "wireframe": false,
            "twoSided": true,
            "depthWrite": true,
            "depthTest": true,
            "transparent": false
          }
        },
        {
          "id": 2,
          "parent": -1,
          "name": "box",
          "visible": true,
          "position": [0, 0, 0],
          "rotation": [0, 0, 0, 1],
          "scale": [1, 1, 1],
          "mesh": { "primitive": "box", "size": 1.5, "segments": 16, "rings": 8 },
          "material": {
            "texture": -1,
            "environment": -1,
            "colour": "#3CB4FF",
            "sourceBlend": "one",
            "destinationBlend": "zero",
            "shading": "unlit",
            "wireframe": false,
            "twoSided": false,
            "depthWrite": true,
            "depthTest": true,
            "transparent": false
          }
        }
      ],
      "animations": [
        {
          "id": 0,
          "name": "spin",
          "tracks": [
            {
              "property": "2.ry",
              "interpolation": "linear",
              "keys": [[0, 0], [2, 3.14159], [4, 6.28318]]
            }
          ]
        }
      ],
      "cameraTracks": [
        {
          "property": "eyeZ",
          "interpolation": "linear",
          "keys": [[0, -6], [5, -2]]
        }
      ]
    }
  ],
  "timeline": [
    { "type": "clear", "start": 0, "end": 500,
      "rect": [0, 0, 800, 600], "rectEnd": [0, 0, 800, 600],
      "colour": "#03060C", "screen": true, "depth": true },

    { "type": "rendertotexture", "start": 0, "end": 500,
      "rect": [0, 0, 800, 600], "rectEnd": [0, 0, 800, 600],
      "slot": 0 },

    { "type": "display3d", "start": 0, "end": 500,
      "rect": [0, 0, 800, 600], "rectEnd": [0, 0, 800, 600],
      "scene": 1, "camera": 0, "animation": 0,
      "cameraStart": 0, "cameraEnd": 5,
      "animationStart": 0, "animationEnd": 4,
      "dontSaveCamera": false },

    { "type": "feedback", "start": 200, "end": 500,
      "rect": [0, 0, 800, 600], "rectEnd": [0, 0, 800, 600],
      "startColour": "#5AFFFFFF", "endColour": "#28FFFFFF",
      "sourceBlend": "one", "destinationBlend": "one",
      "slot": 0, "layers": 3, "zoom": 10 }
  ]
}
```

The texture decodes to four identical pixels of `0xFF102030` (dark blue), which is enough to see
that the sampling path works.

Note that `clear`, `rendertotexture` and `display3d` all start at frame 0 and are listed in the
order they must run. The timeline sort is not stable, so a project that depends on that order
should give the effects different start frames instead.
