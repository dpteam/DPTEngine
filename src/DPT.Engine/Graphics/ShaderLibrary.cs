//
// DPT.Engine - HLSL shader sources.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

namespace DPT.Engine.Graphics
{
    /// <summary>
    /// The engine's shaders, as source.
    ///
    /// The original shipped Xbox 360 bytecode (.vso.xbox / .pso.xbox) that no PC
    /// driver can load, so the port carries HLSL and compiles it at start-up
    /// (see <see cref="ShaderCompiler"/>). The shader set is the same one the
    /// original used - one vertex shader per vertex layout, one pixel shader per
    /// material and post-process - so the rendering code reads like the
    /// original's.
    ///
    /// Register layout is fixed and shared with <see cref="D3D9.Registers"/>:
    ///
    ///   vertex   c0..c3  projection
    ///            c4..c7  view
    ///            c8..c11 world
    ///            c12..c15 texture transform (2D passes only)
    ///            c16     fog range (x = start, y = end)
    ///
    ///   pixel    c0      global colour
    ///            c1      effect parameter 1 (fog colour, blur step, contrast mul)
    ///            c2      effect parameter 2 (contrast add)
    ///
    /// Shader model is vs_2_0 / ps_2_0: every Direct3D 9 capable card supports it,
    /// including the Radeon 9500-era hardware this engine is meant to run on, and
    /// the reference rasteriser. ps_3_0 would be faster on modern cards but would
    /// drop the low end for no visual gain in these shaders.
    /// </summary>
    internal static class ShaderLibrary
    {
        internal const string VertexModel = "vs_2_0";
        internal const string PixelModel = "ps_2_0";

        // ---------------------------------------------------------------- vertex

        /// <summary>
        /// Position + normal + texture coordinate: the object vertex.
        ///
        /// Also emits the view-space depth that the fog shader needs and the
        /// world-space normal that the environment-map shader needs.
        /// </summary>
        internal const string VertexPositionNormalTexture = @"
float4x4 Projection     : register(c0);
float4x4 View           : register(c4);
float4x4 World          : register(c8);
float4   FogRange       : register(c16);

struct VS_INPUT
{
    float4 Position : POSITION;
    float3 Normal   : NORMAL;
    float2 Tex      : TEXCOORD0;
};

struct VS_OUTPUT
{
    float4 Position : POSITION;
    float2 Tex      : TEXCOORD0;
    float3 Normal   : TEXCOORD1;
    float  Depth    : TEXCOORD2;
};

VS_OUTPUT main(VS_INPUT input)
{
    VS_OUTPUT output;

    float4 world = mul(input.Position, World);
    float4 view  = mul(world, View);

    output.Position = mul(view, Projection);
    output.Tex      = input.Tex;
    output.Normal   = mul(input.Normal, World);
    output.Depth    = view.z;

    return output;
}
";

        /// <summary>
        /// Position + texture coordinate, with the texture transform applied.
        ///
        /// The 2D passes (render-target copy, blur, feedback) position their quad
        /// with the world matrix and map the source rectangle with the texture
        /// matrix, exactly as the original did.
        /// </summary>
        internal const string VertexPositionTexture = @"
float4x4 Projection     : register(c0);
float4x4 View           : register(c4);
float4x4 World          : register(c8);
float4x4 TexTransform   : register(c12);

struct VS_INPUT
{
    float4 Position : POSITION;
    float2 Tex      : TEXCOORD0;
};

struct VS_OUTPUT
{
    float4 Position : POSITION;
    float2 Tex      : TEXCOORD0;
};

VS_OUTPUT main(VS_INPUT input)
{
    VS_OUTPUT output;

    float4 world = mul(input.Position, World);
    output.Position = mul(mul(world, View), Projection);

    float4 transformed = mul(float4(input.Tex, 0.0f, 1.0f), TexTransform);
    output.Tex = transformed.xy;

    return output;
}
";

        /// <summary>Position + texture coordinate + per-vertex colour: textured particles.</summary>
        internal const string VertexPositionTextureColor = @"
float4x4 Projection     : register(c0);
float4x4 View           : register(c4);
float4x4 World          : register(c8);

struct VS_INPUT
{
    float4 Position : POSITION;
    float4 Color    : COLOR0;
    float2 Tex      : TEXCOORD0;
};

struct VS_OUTPUT
{
    float4 Position : POSITION;
    float4 Color    : COLOR0;
    float2 Tex      : TEXCOORD0;
};

VS_OUTPUT main(VS_INPUT input)
{
    VS_OUTPUT output;

    float4 world = mul(input.Position, World);
    output.Position = mul(mul(world, View), Projection);
    output.Color = input.Color;
    output.Tex = input.Tex;

    return output;
}
";

        /// <summary>Position + per-vertex colour: particle tails, debug lines.</summary>
        internal const string VertexPositionColor = @"
float4x4 Projection     : register(c0);
float4x4 View           : register(c4);
float4x4 World          : register(c8);

struct VS_INPUT
{
    float4 Position : POSITION;
    float4 Color    : COLOR0;
};

struct VS_OUTPUT
{
    float4 Position : POSITION;
    float4 Color    : COLOR0;
};

VS_OUTPUT main(VS_INPUT input)
{
    VS_OUTPUT output;

    float4 world = mul(input.Position, World);
    output.Position = mul(mul(world, View), Projection);
    output.Color = input.Color;

    return output;
}
";

        // ---------------------------------------------------------------- pixel

        /// <summary>Flat colour: the untextured layer.</summary>
        internal const string PixelColor = @"
float4 GlobalColor : register(c0);

float4 main() : COLOR
{
    return GlobalColor;
}
";

        /// <summary>Texture modulated by the global colour.</summary>
        internal const string PixelTextureColor = @"
sampler2D Texture0    : register(s0);
float4    GlobalColor : register(c0);

float4 main(float2 texCoord : TEXCOORD0) : COLOR
{
    return tex2D(Texture0, texCoord) * GlobalColor;
}
";

        /// <summary>
        /// Texture modulated by an environment map and the global colour.
        ///
        /// The environment coordinate is the world-space normal projected to the
        /// unit square - a sphere map. It is what the original's
        /// texturecolorenvmap shader did, and it is why objects with an
        /// environment map look chrome-ish without any per-pixel reflection math.
        /// </summary>
        internal const string PixelTextureEnvColor = @"
sampler2D Texture0    : register(s0);
sampler2D Texture1    : register(s1);
float4    GlobalColor : register(c0);

float4 main(float2 texCoord : TEXCOORD0, float3 normal : TEXCOORD1) : COLOR
{
    float2 envCoord = normalize(normal).xy * 0.5f + 0.5f;
    float4 base = tex2D(Texture0, texCoord);
    float4 env  = tex2D(Texture1, envCoord);

    return base * env * GlobalColor;
}
";

        /// <summary>Environment map with linear fog.</summary>
        internal const string PixelTextureEnvColorFog = @"
sampler2D Texture0    : register(s0);
sampler2D Texture1    : register(s1);
float4    GlobalColor : register(c0);
float4    FogColor    : register(c1);
float4    FogRange    : register(c16);

float4 main(float2 texCoord : TEXCOORD0, float3 normal : TEXCOORD1, float depth : TEXCOORD2) : COLOR
{
    float2 envCoord = normalize(normal).xy * 0.5f + 0.5f;
    float4 base = tex2D(Texture0, texCoord);
    float4 env  = tex2D(Texture1, envCoord);
    float4 color = base * env * GlobalColor;

    // Linear fog on view-space depth. The range comes from the world's fog
    // settings; a zero-width range would divide by zero, so it is clamped.
    float span = max(FogRange.y - FogRange.x, 0.0001f);
    float fog  = saturate((depth - FogRange.x) / span);

    return lerp(color, FogColor, fog);
}
";

        /// <summary>Per-vertex colour only: particle tails.</summary>
        internal const string PixelDiffuse = @"
float4 main(float4 color : COLOR0) : COLOR
{
    return color;
}
";

        /// <summary>Texture modulated by per-vertex colour: particle heads.</summary>
        internal const string PixelTextureDiffuse = @"
sampler2D Texture0 : register(s0);

float4 main(float2 texCoord : TEXCOORD0, float4 color : COLOR0) : COLOR
{
    return tex2D(Texture0, texCoord) * color;
}
";

        /// <summary>
        /// Separable blur used by the blur event.
        ///
        /// Four taps in a cross pattern, scaled by c1.x, which the event raises
        /// with each successive layer. Not a gaussian: at these layer counts a
        /// cross is indistinguishable and costs a quarter of the samples.
        /// </summary>
        internal const string PixelBlur = @"
sampler2D Texture0 : register(s0);
float4    GlobalColor : register(c0);
float4    BlurParams  : register(c1);

float4 main(float2 texCoord : TEXCOORD0) : COLOR
{
    float2 step = BlurParams.x;

    float4 sum = tex2D(Texture0, texCoord + float2( step, 0.0f));
    sum += tex2D(Texture0, texCoord + float2(-step, 0.0f));
    sum += tex2D(Texture0, texCoord + float2(0.0f,  step));
    sum += tex2D(Texture0, texCoord + float2(0.0f, -step));

    return (sum * 0.25f) * GlobalColor;
}
";

        /// <summary>
        /// Feedback: samples the previous frame at a slightly different scale so
        /// the result smears outward. The scaling is done with the texture
        /// matrix, so this shader only has to tint.
        /// </summary>
        internal const string PixelFeedback = @"
sampler2D Texture0 : register(s0);
float4    GlobalColor : register(c0);

float4 main(float2 texCoord : TEXCOORD0) : COLOR
{
    return tex2D(Texture0, texCoord) * GlobalColor;
}
";

        /// <summary>
        /// Contrast: pushes values away from mid-grey by a multiplier and then
        /// shifts them by an offset.
        ///
        /// c1.x is the multiplier, c1.y the offset, both pre-biased by one at the
        /// call site so that "0" means "no change" in the project file.
        /// </summary>
        internal const string PixelContrast = @"
sampler2D Texture0 : register(s0);
float4    GlobalColor : register(c0);
float4    Contrast    : register(c1);

float4 main(float2 texCoord : TEXCOORD0) : COLOR
{
    float4 color = tex2D(Texture0, texCoord);

    color.rgb = (color.rgb - 0.5f) * Contrast.x + 0.5f + (Contrast.y - 1.0f);

    return saturate(color) * GlobalColor;
}
";

        /// <summary>
        /// Screen-space textured quad with no modulation: used to copy a render
        /// target back to the frame buffer at 1:1.
        /// </summary>
        internal const string PixelTexture = @"
sampler2D Texture0 : register(s0);

float4 main(float2 texCoord : TEXCOORD0) : COLOR
{
    return tex2D(Texture0, texCoord);
}
";

        // ---------------------------------------------------------------- registry

        /// <summary>
        /// One entry per shader the renderer can ask for.
        ///
        /// A string key rather than an enum so the editor can name a custom
        /// shader without the runtime needing a recompile.
        /// </summary>
        internal static bool TryGetVertex(string name, out string source, out string entryPoint)
        {
            entryPoint = "main";

            switch (name)
            {
                case "posnormaltex":
                    source = VertexPositionNormalTexture;
                    return true;
                case "postex":
                    source = VertexPositionTexture;
                    return true;
                case "postexcolor":
                    source = VertexPositionTextureColor;
                    return true;
                case "poscolor":
                    source = VertexPositionColor;
                    return true;
                default:
                    source = null;
                    return false;
            }
        }

        internal static bool TryGetPixel(string name, out string source, out string entryPoint)
        {
            entryPoint = "main";

            switch (name)
            {
                case "color":
                    source = PixelColor;
                    return true;
                case "texturecolor":
                    source = PixelTextureColor;
                    return true;
                case "texturecolorenvmap":
                    source = PixelTextureEnvColor;
                    return true;
                case "texturecolorenvmapfog":
                    source = PixelTextureEnvColorFog;
                    return true;
                case "diffuse":
                    source = PixelDiffuse;
                    return true;
                case "texturediffuse":
                    source = PixelTextureDiffuse;
                    return true;
                case "blur":
                    source = PixelBlur;
                    return true;
                case "feedback":
                    source = PixelFeedback;
                    return true;
                case "contrast":
                    source = PixelContrast;
                    return true;
                case "texture":
                    source = PixelTexture;
                    return true;
                default:
                    source = null;
                    return false;
            }
        }
    }
}
