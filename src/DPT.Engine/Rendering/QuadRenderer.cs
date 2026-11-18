//
// DPT.Engine - screen-space quad drawing.
// Copyright (C) 2026 DartPower Team. GPLv2.
//

using System;
using System.Runtime.InteropServices;

using DPT.Engine.Graphics;
using DPT.Engine.Mathematics;
using DPT.Engine.Scenes;

namespace DPT.Engine.Rendering
{
    /// <summary>
    /// Draws one textured or coloured rectangle in screen space.
    ///
    /// This is the building block for every post-process in the engine: the blur,
    /// the feedback, the contrast pass and the render-target copy are all "draw a
    /// quad with a shader". Keeping them on one path means one place to get the
    /// blend state, the texture matrix and the viewport right.
    ///
    /// Geometry is a unit quad scaled into place by the world matrix and mapped
    /// into the source texture by the texture matrix, exactly as the original
    /// engine did - which is why the same texture-matrix arithmetic produces the
    /// same zoom and offset behaviour in the effects.
    /// </summary>
    internal sealed class QuadRenderer : IDisposable
    {
        private readonly D3D9Device device;

        private D3D9VertexBuffer vertices;
        private D3D9VertexShader vertexShader;
        private D3D9PixelShader textureShader;
        private D3D9PixelShader colourShader;
        private D3D9PixelShader blurShader;
        private D3D9PixelShader feedbackShader;
        private D3D9PixelShader contrastShader;

        private bool ready;
        private bool disposed;

        internal QuadRenderer(D3D9Device device)
        {
            if (device == null) throw new ArgumentNullException("device");
            this.device = device;
        }

        internal bool IsReady
        {
            get { return ready; }
        }

        /// <summary>Compiles the shaders and uploads the quad. Idempotent.</summary>
        internal void Initialize()
        {
            if (ready) return;

            string source;
            string entry;

            ShaderLibrary.TryGetVertex("postex", out source, out entry);
            vertexShader = device.CompileVertexShader("quad.vs", source, entry);

            ShaderLibrary.TryGetPixel("texture", out source, out entry);
            textureShader = device.CompilePixelShader("quad.texture", source, entry);

            ShaderLibrary.TryGetPixel("color", out source, out entry);
            colourShader = device.CompilePixelShader("quad.color", source, entry);

            ShaderLibrary.TryGetPixel("blur", out source, out entry);
            blurShader = device.CompilePixelShader("quad.blur", source, entry);

            ShaderLibrary.TryGetPixel("feedback", out source, out entry);
            feedbackShader = device.CompilePixelShader("quad.feedback", source, entry);

            ShaderLibrary.TryGetPixel("contrast", out source, out entry);
            contrastShader = device.CompilePixelShader("quad.contrast", source, entry);

            // A unit quad in the XY plane, wound clockwise when seen from +Z.
            var quad = new[]
            {
                new VertexPositionNormalTexture(0f, 0f, 0f, 0f, 0f),
                new VertexPositionNormalTexture(1f, 0f, 0f, 1f, 0f),
                new VertexPositionNormalTexture(1f, 1f, 0f, 1f, 1f),
                new VertexPositionNormalTexture(0f, 1f, 0f, 0f, 1f)
            };

            for (var i = 0; i < quad.Length; i++)
            {
                quad[i].NZ = -1f;
            }

            var bytes = SceneRenderer.StructsToBytes(quad);

            vertices = device.CreateVertexBuffer(bytes.Length, VertexPositionNormalTexture.Fvf, false);
            vertices.Upload(bytes, 0);

            ready = true;
        }

        /// <summary>
        /// Sets up screen-space rendering: identity view, an orthographic
        /// projection over the back buffer, depth off.
        ///
        /// Called before a run of quads; <see cref="EndScreenSpace"/> restores
        /// nothing, because the renderer sets up its own camera before the next
        /// 3D pass.
        /// </summary>
        internal void BeginScreenSpace(int width, int height)
        {
            var identity = Matrix.Identity;
            var projection = Matrix.CreateOrthographicOffCenter(0f, width, height, 0f, -1f, 1f);

            device.SetVertexShaderConstant(D3D9.Registers.View, ref identity);
            device.SetVertexShaderConstant(D3D9.Registers.Projection, ref projection);
            device.SetVertexShaderConstant(D3D9.Registers.TextureTransform, ref identity);

            device.SetRenderState(D3D9.RenderState.ZEnable, 0u);
            device.SetRenderState(D3D9.RenderState.ZWriteEnable, 0u);
            device.SetRenderState(D3D9.RenderState.CullMode, (uint)D3D9.Cull.None);
            device.SetRenderState(D3D9.RenderState.Lighting, 0u);

            device.SetSamplerState(0, D3D9.SamplerState.AddressU, (uint)D3D9.TextureAddress.Clamp);
            device.SetSamplerState(0, D3D9.SamplerState.AddressV, (uint)D3D9.TextureAddress.Clamp);
        }

        /// <summary>Draws a solid or textured rectangle with a blend mode.</summary>
        internal void Draw(D3D9Texture texture, int x, int y, int width, int height,
            ColorRgba colour, BlendMode sourceBlend, BlendMode destinationBlend)
        {
            Draw(texture, x, y, width, height, colour, sourceBlend, destinationBlend,
                QuadShader.Texture, Matrix.Identity);
        }

        internal void Draw(D3D9Texture texture, int x, int y, int width, int height,
            ColorRgba colour, BlendMode sourceBlend, BlendMode destinationBlend,
            QuadShader shader, Matrix textureMatrix)
        {
            if (!ready) Initialize();

            var world = Matrix.CreateScale(width, height, 1f);
            world.M41 = x;
            world.M42 = y;

            device.SetVertexShaderConstant(D3D9.Registers.World, ref world);
            device.SetVertexShaderConstant(D3D9.Registers.TextureTransform, ref textureMatrix);

            device.SetFvf(VertexPositionNormalTexture.Fvf);
            device.SetVertexShader(vertexShader);

            switch (shader)
            {
                case QuadShader.Blur:
                    device.SetPixelShader(blurShader);
                    break;
                case QuadShader.Feedback:
                    device.SetPixelShader(feedbackShader);
                    break;
                case QuadShader.Contrast:
                    device.SetPixelShader(contrastShader);
                    break;
                default:
                    device.SetPixelShader(texture == null ? colourShader : textureShader);
                    break;
            }

            device.SetTexture(0, texture);
            device.SetTexture(1, null);

            var blended = sourceBlend != BlendMode.One || destinationBlend != BlendMode.Zero;

            device.SetRenderState(D3D9.RenderState.AlphaBlendEnable, blended);
            device.SetRenderState(D3D9.RenderState.SrcBlend, ToD3DBlend(sourceBlend));
            device.SetRenderState(D3D9.RenderState.DestBlend, ToD3DBlend(destinationBlend));

            device.SetPixelShaderConstant(D3D9.Registers.PixelColor,
                new Vector4(colour.R / 255f, colour.G / 255f, colour.B / 255f, colour.A / 255f));

            device.SetStreamSource(0, vertices, VertexPositionNormalTexture.SizeInBytes);
            device.SetIndices(null);

            device.DrawPrimitive(D3D9.PrimitiveType.TriangleStrip, 0, 2);
        }

        /// <summary>Sets the per-effect parameters the post-process shaders read.</summary>
        internal void SetEffectParameters(Vector4 parameters1, Vector4 parameters2)
        {
            device.SetPixelShaderConstant(D3D9.Registers.PixelParams1, parameters1);
            device.SetPixelShaderConstant(D3D9.Registers.PixelParams2, parameters2);
        }

        private static uint ToD3DBlend(BlendMode mode)
        {
            switch (mode)
            {
                case BlendMode.Zero: return (uint)D3D9.Blend.Zero;
                case BlendMode.One: return (uint)D3D9.Blend.One;
                case BlendMode.SourceColour: return (uint)D3D9.Blend.SourceColor;
                case BlendMode.InverseSourceColour: return (uint)D3D9.Blend.InverseSourceColor;
                case BlendMode.DestinationAlpha: return (uint)D3D9.Blend.DestinationAlpha;
                case BlendMode.InverseDestinationAlpha: return (uint)D3D9.Blend.InverseDestinationAlpha;
                case BlendMode.DestinationColour: return (uint)D3D9.Blend.DestinationColor;
                case BlendMode.InverseDestinationColour: return (uint)D3D9.Blend.InverseDestinationColor;
                case BlendMode.SourceAlphaSaturation: return (uint)D3D9.Blend.SourceAlphaSaturation;
                default: return (uint)D3D9.Blend.SourceAlpha;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            ReleaseDeviceResources();
        }

        internal void ReleaseDeviceResources()
        {
            if (vertices != null) { vertices.Dispose(); vertices = null; }
            if (vertexShader != null) { vertexShader.Dispose(); vertexShader = null; }
            if (textureShader != null) { textureShader.Dispose(); textureShader = null; }
            if (colourShader != null) { colourShader.Dispose(); colourShader = null; }
            if (blurShader != null) { blurShader.Dispose(); blurShader = null; }
            if (feedbackShader != null) { feedbackShader.Dispose(); feedbackShader = null; }
            if (contrastShader != null) { contrastShader.Dispose(); contrastShader = null; }

            ready = false;
        }
    }

    /// <summary>Which post-process shader a quad uses.</summary>
    internal enum QuadShader
    {
        Texture,
        Blur,
        Feedback,
        Contrast
    }
}
