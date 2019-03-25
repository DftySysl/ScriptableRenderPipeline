using System;
using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    public partial class HDRenderPipeline
    {
        public void InitializePrepass(HDRenderPipelineAsset hdAsset)
        {
            m_DepthResolveMaterial = CoreUtils.CreateEngineMaterial(asset.renderPipelineResources.shaders.depthValuesPS);
        }

        public void CleanupPrepass()
        {
            CoreUtils.Destroy(m_DepthResolveMaterial);
        }

        struct PrepassOutput
        {
            public GBufferOutput       gbuffer;
            public RenderGraphResource depthValuesMSAA;
        }

        PrepassOutput RenderPrepass(RenderGraph renderGraph, CullingResults cullingResults, HDCamera hdCamera)
        {
            var result = new PrepassOutput();

            bool renderMotionVectorAfterGBuffer = RenderDepthPrepass(m_RenderGraph, cullingResults, hdCamera);

            if (!renderMotionVectorAfterGBuffer)
            {
                // If objects velocity if enabled, this will render the objects with motion vector into the target buffers (in addition to the depth)
                // Note: An object with motion vector must not be render in the prepass otherwise we can have motion vector write that should have been rejected
                RenderObjectsVelocityPass(m_RenderGraph, cullingResults, hdCamera);
            }

            // At this point in forward all objects have been rendered to the prepass (depth/normal/velocity) so we can resolve them
            result.depthValuesMSAA = ResolvePrepassBuffers(m_RenderGraph, hdCamera);

            /*
            // This will bind the depth buffer if needed for DBuffer)
            RenderDBuffer(hdCamera, cmd, renderContext, cullingResults);
            // We can call DBufferNormalPatch after RenderDBuffer as it only affect forward material and isn't affected by RenderGBuffer
            // This reduce lifteime of stencil bit
            DBufferNormalPatch(hdCamera, cmd, renderContext, cullingResults);

#if ENABLE_RAYTRACING
            bool raytracedIndirectDiffuse = m_RaytracingIndirectDiffuse.RenderIndirectDiffuse(hdCamera, cmd, renderContext, m_FrameCount);
            PushFullScreenDebugTexture(hdCamera, cmd, m_RaytracingIndirectDiffuse.GetIndirectDiffuseTexture(), FullScreenDebugMode.IndirectDiffuse);
            cmd.SetGlobalInt(HDShaderIDs._RaytracedIndirectDiffuse, raytracedIndirectDiffuse ? 1 : 0);
#endif
*/

            result.gbuffer = RenderGBuffer(m_RenderGraph, cullingResults, hdCamera);

            // In both forward and deferred, everything opaque should have been rendered at this point so we can safely copy the depth buffer for later processing.
            GenerateDepthPyramid(renderGraph, hdCamera, FullScreenDebugMode.DepthPyramid);

            if (renderMotionVectorAfterGBuffer)
            {
                // See the call RenderObjectsVelocity() above and comment
                RenderObjectsVelocityPass(m_RenderGraph, cullingResults, hdCamera);
            }

            return result;
        }

        class DepthPrepassData : RenderPassData
        {
            public FrameSettings frameSettings;
            public Rect renderingViewport;
            public bool msaaEnabled;

            public RenderGraphMutableResource depthBuffer;
            public RenderGraphMutableResource depthAsColorBuffer;
            public RenderGraphMutableResource normalBuffer;

            public RenderGraphResource rendererList1;
            public RenderGraphResource rendererList2;
        }

        // RenderDepthPrepass render both opaque and opaque alpha tested based on engine configuration.
        // Lit Forward only: We always render all materials
        // Lit Deferred: We always render depth prepass for alpha tested (optimization), other deferred material are render based on engine configuration.
        // Forward opaque with deferred renderer (DepthForwardOnly pass): We always render all materials
        // True is returned if motion vector must be rendered after GBuffer pass
        bool RenderDepthPrepass(RenderGraph renderGraph, CullingResults cull, HDCamera hdCamera)
        {
            // Guidelines:
            // Lit shader can be in deferred or forward mode. In this case we use "DepthOnly" pass with "GBuffer" or "Forward" pass name
            // Other shader, including unlit are always forward and use "DepthForwardOnly" with "ForwardOnly" pass.
            // Those pass are exclusive so use only "DepthOnly" or "DepthForwardOnly" but not both at the same time, same for "Forward" and "DepthForwardOnly"
            // Any opaque material rendered in forward should have a depth prepass. If there is no depth prepass the lighting will be incorrect (deferred shadowing, contact shadow, SSAO), this may be acceptable depends on usage

            // Whatever the configuration we always render first opaque object then opaque alpha tested as they are more costly to render and could be reject by early-z
            // (but no Hi-z as it is disable with clip instruction). This is handled automatically with the RenderQueue value (OpaqueAlphaTested have a different value and thus are sorted after Opaque)

            // Forward material always output normal buffer.
            // Deferred material never output normal buffer.
            // Caution: Unlit material let normal buffer untouch. Caution as if people try to filter normal buffer, it can result in weird result.
            // TODO: Do we need a stencil bit to identify normal buffer not fill by unlit? So don't execute SSAO / SRR ?

            // Additional guidelines for motion vector:
            // We render object motion vector at the same time than depth prepass with MRT to save drawcall. Depth buffer is then fill with combination of depth prepass + motion vector.
            // For this we render first all objects that render depth only, then object that require object motion vector.
            // We use the excludeMotion filter option of DrawRenderer to gather object without object motion vector (only C++ can know if an object have object motion vector).
            // Caution: if there is no depth prepass we must render object motion vector after GBuffer pass otherwise some depth only objects can hide objects with motion vector and overwrite depth buffer but not update
            // the motion vector buffer resulting in artifacts

            bool fullDeferredPrepass = hdCamera.frameSettings.IsEnabled(FrameSettingsField.DepthPrepassWithDeferredRendering) || m_DbufferManager.enableDecals;
            // To avoid rendering objects twice (once in the depth pre-pass and once in the motion vector pass when the motion vector pass is enabled) we exclude the objects that have motion vectors.
            bool objectMotionEnabled = hdCamera.frameSettings.IsEnabled(FrameSettingsField.ObjectMotionVectors);
            bool shouldRenderMotionVectorAfterGBuffer = (hdCamera.frameSettings.litShaderMode == LitShaderMode.Deferred) && !fullDeferredPrepass;
            bool msaa = hdCamera.frameSettings.IsEnabled(FrameSettingsField.MSAA);

            switch (hdCamera.frameSettings.litShaderMode)
            {
                case LitShaderMode.Forward:
                    using (var builder = renderGraph.AddRenderPass<DepthPrepassData>("Depth Prepass (forward)", out var passData, CustomSamplerId.DepthPrepass.GetSampler()))
                    {
                        passData.frameSettings = hdCamera.frameSettings;
                        passData.msaaEnabled = msaa;

                        passData.depthBuffer = builder.UseDepthBuffer(GetDepthStencilBuffer(msaa));
                        passData.normalBuffer = builder.UseColorBuffer(GetNormalBuffer(msaa), 0);
                        if (msaa)
                            passData.depthAsColorBuffer = builder.UseColorBuffer(GetDepthTexture(true), 1);

                        // Full forward: Output normal buffer for both forward and forwardOnly
                        // Exclude object that render velocity (if motion vector are enabled)
                        passData.rendererList1 = builder.UseRendererList(
                            builder.CreateRendererList(new RendererListDesc(m_DepthOnlyAndDepthForwardOnlyPassNames, cull, hdCamera.camera)
                            {
                                renderQueueRange = HDRenderQueue.k_RenderQueue_AllOpaque,
                                sortingCriteria = SortingCriteria.CommonOpaque,
                                excludeMotionVectors = objectMotionEnabled
                            }));

                        builder.SetRenderFunc(
                        (RenderPassData data, RenderGraphGlobalParams globalParams, RenderGraphContext renderGraphContext) =>
                        {
                            DepthPrepassData prepassData = (DepthPrepassData)data;
                            DrawOpaqueRendererList(prepassData.frameSettings, renderGraphContext.resources.GetRendererList(prepassData.rendererList1), renderGraphContext.renderContext, renderGraphContext.cmd);
                        });
                    }
                    break;
                case LitShaderMode.Deferred:
                    string passName = fullDeferredPrepass ? (m_DbufferManager.enableDecals ? "Depth Prepass (deferred) forced by Decals" : "Depth Prepass (deferred)") : "Depth Prepass (deferred incomplete)";
                    bool excludeMotion = fullDeferredPrepass ? objectMotionEnabled : false;

                    // First deferred alpha tested materials. Alpha tested object have always a prepass even if enableDepthPrepassWithDeferredRendering is disabled
                    var partialPrepassRenderQueueRange = new RenderQueueRange { lowerBound = (int)RenderQueue.AlphaTest, upperBound = (int)RenderQueue.GeometryLast - 1 };

                    using (var builder = renderGraph.AddRenderPass<DepthPrepassData>(passName, out var passData, CustomSamplerId.DepthPrepass.GetSampler()))
                    {
                        passData.frameSettings = hdCamera.frameSettings;
                        passData.renderingViewport = hdCamera.renderingViewport;
                        passData.msaaEnabled = msaa;

                        // First deferred material
                        passData.rendererList1 = builder.UseRendererList(
                            builder.CreateRendererList(new RendererListDesc(m_DepthOnlyPassNames, cull, hdCamera.camera)
                            {
                                renderQueueRange = fullDeferredPrepass ? HDRenderQueue.k_RenderQueue_AllOpaque : partialPrepassRenderQueueRange,
                                sortingCriteria = SortingCriteria.CommonOpaque,
                                excludeMotionVectors = excludeMotion
                            }));

                        // Then forward only material that output normal buffer
                        passData.rendererList2 = builder.UseRendererList(
                            builder.CreateRendererList(new RendererListDesc(m_DepthForwardOnlyPassNames, cull, hdCamera.camera)
                            {
                                renderQueueRange = HDRenderQueue.k_RenderQueue_AllOpaque,
                                sortingCriteria = SortingCriteria.CommonOpaque,
                                excludeMotionVectors = excludeMotion
                            }));

                        passData.depthBuffer = builder.WriteTexture(GetDepthStencilBuffer(msaa));
                        passData.normalBuffer = builder.WriteTexture(GetNormalBuffer(msaa));
                        if (msaa)
                            passData.depthAsColorBuffer = builder.WriteTexture(GetDepthTexture(true));

                        builder.SetRenderFunc(
                        (RenderPassData data, RenderGraphGlobalParams globalParams, RenderGraphContext renderGraphContext) =>
                        {
                            DepthPrepassData prepassData = (DepthPrepassData)data;

                            HDUtils.SetRenderTarget(renderGraphContext.cmd, prepassData.renderingViewport, renderGraphContext.resources.GetTexture(prepassData.depthBuffer));
                            // XRTODO: wait for XR SDK integration and implement custom version in HDUtils with dynamic resolution support
                            //XRUtils.DrawOcclusionMesh(cmd, hdCamera.camera, hdCamera.camera.stereoEnabled);
                            DrawOpaqueRendererList(prepassData.frameSettings, renderGraphContext.resources.GetRendererList(prepassData.rendererList1), renderGraphContext.renderContext, renderGraphContext.cmd);

                            var mrt = RenderGraphUtils.GetMRTArray(prepassData.msaaEnabled ? 2 : 1);
                            mrt[0] = renderGraphContext.resources.GetTexture(prepassData.normalBuffer);
                            if (prepassData.msaaEnabled)
                                mrt[1] = renderGraphContext.resources.GetTexture(prepassData.depthAsColorBuffer);

                            HDUtils.SetRenderTarget(renderGraphContext.cmd, prepassData.renderingViewport, mrt, renderGraphContext.resources.GetTexture(prepassData.depthBuffer));
                            DrawOpaqueRendererList(prepassData.frameSettings, renderGraphContext.resources.GetRendererList(prepassData.rendererList2), renderGraphContext.renderContext, renderGraphContext.cmd);
                        });
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException("Unknown ShaderLitMode");
            }

    #if ENABLE_RAYTRACING
                // If there is a ray-tracing environment and the feature is enabled we want to push these objects to the prepass
                HDRaytracingEnvironment currentEnv = m_RayTracingManager.CurrentEnvironment();
                // We want the opaque objects to be in the prepass so that we avoid rendering uselessly the pixels before raytracing them
                if (currentEnv != null && currentEnv.raytracedObjects)
                    RenderOpaqueRenderList(cull, hdCamera, renderContext, cmd, m_DepthOnlyAndDepthForwardOnlyPassNames, 0, HDRenderQueue.k_RenderQueue_AllOpaqueRaytracing);
    #endif

            return shouldRenderMotionVectorAfterGBuffer;
        }

        class ObjectVelocityPassData : RenderPassData
        {
            public FrameSettings                frameSettings;
            public RenderGraphMutableResource   depthBuffer;
            public RenderGraphMutableResource   velocityBuffer;
            public RenderGraphMutableResource   normalBuffer;
            public RenderGraphMutableResource   depthAsColorMSAABuffer;
            public RenderGraphResource          rendererList;
        }

        void RenderObjectsVelocityPass(RenderGraph renderGraph, CullingResults cull, HDCamera hdCamera)
        {
            if (!hdCamera.frameSettings.IsEnabled(FrameSettingsField.ObjectMotionVectors))
                return;

            using (var builder = renderGraph.AddRenderPass<ObjectVelocityPassData>("Objects Velocity", out var passData, CustomSamplerId.ObjectsVelocity.GetSampler()))
            {
                bool msaa = hdCamera.frameSettings.IsEnabled(FrameSettingsField.MSAA);

                passData.frameSettings = hdCamera.frameSettings;
                passData.depthBuffer = builder.UseDepthBuffer(GetDepthStencilBuffer(msaa));
                passData.velocityBuffer = builder.UseColorBuffer(GetVelocityBuffer(msaa), 0);
                passData.normalBuffer = builder.UseColorBuffer(GetNormalBuffer(msaa), 1);
                if (msaa)
                    passData.depthAsColorMSAABuffer = builder.UseColorBuffer(GetDepthTexture(msaa), 2);

                passData.rendererList = builder.UseRendererList(
                    builder.CreateRendererList(new RendererListDesc(HDShaderPassNames.s_MotionVectorsName, cull, hdCamera.camera)
                    {
                        renderQueueRange = HDRenderQueue.k_RenderQueue_AllOpaque,
                        sortingCriteria = SortingCriteria.CommonOpaque,
                        rendererConfiguration = PerObjectData.MotionVectors
                    }));

                builder.SetRenderFunc(
                (RenderPassData data, RenderGraphGlobalParams globalParams, RenderGraphContext renderGraphContext) =>
                {
                    ObjectVelocityPassData prepassData = (ObjectVelocityPassData)data;
                    DrawOpaqueRendererList(prepassData.frameSettings, renderGraphContext.resources.GetRendererList(prepassData.rendererList), renderGraphContext.renderContext, renderGraphContext.cmd);
                });
            }
        }

        class GBufferPassData : RenderPassData
        {
            public FrameSettings                frameSettings;
            public RenderGraphResource          rendererList;
            public RenderGraphMutableResource[] gbufferRT = new RenderGraphMutableResource[RenderGraphUtils.kMaxMRTCount];
            public RenderGraphMutableResource   depthBuffer;
        }

        struct GBufferOutput
        {
            public RenderGraphResource[] gbuffer;
        }

        void SetupGBufferTargets(GBufferPassData passData, ref GBufferOutput output, FrameSettings frameSettings, RenderGraphBuilder builder)
        {
            bool clearGBuffer = NeedClearGBuffer();
            bool lightLayers = frameSettings.IsEnabled(FrameSettingsField.LightLayers);
            bool shadowMasks = frameSettings.IsEnabled(FrameSettingsField.ShadowMask);

            passData.depthBuffer = builder.UseDepthBuffer(GetDepthStencilBuffer());
            passData.gbufferRT[0] = builder.UseColorBuffer(builder.CreateTexture(
                new TextureDesc(Vector2.one) { colorFormat = GraphicsFormat.R8G8B8A8_SRGB, xrInstancing = true, useDynamicScale = true, clearBuffer = clearGBuffer, clearColor = Color.clear, name = "GBuffer0" }, HDShaderIDs._GBufferTexture[0]), 0);
            passData.gbufferRT[1] = builder.UseColorBuffer(GetNormalBuffer(), 1);
            passData.gbufferRT[2] = builder.UseColorBuffer(builder.CreateTexture(
                new TextureDesc(Vector2.one) { colorFormat = GraphicsFormat.R8G8B8A8_UNorm, xrInstancing = true, useDynamicScale = true, clearBuffer = clearGBuffer, clearColor = Color.clear, name = "GBuffer2" }, HDShaderIDs._GBufferTexture[2]), 2);
            passData.gbufferRT[3] = builder.UseColorBuffer(builder.CreateTexture(
                new TextureDesc(Vector2.one) { colorFormat = Builtin.GetLightingBufferFormat(), xrInstancing = true, useDynamicScale = true, clearBuffer = clearGBuffer, clearColor = Color.clear, name = "GBuffer3" }, HDShaderIDs._GBufferTexture[3]), 3);

            int currentIndex = 4;
            if (lightLayers)
            {
                passData.gbufferRT[currentIndex] = builder.UseColorBuffer(builder.CreateTexture(
                    new TextureDesc(Vector2.one) { colorFormat = GraphicsFormat.R8G8B8A8_UNorm, xrInstancing = true, useDynamicScale = true, clearBuffer = clearGBuffer, clearColor = Color.clear, name = "LightLayers" }, HDShaderIDs._LightLayersTexture), currentIndex);
                currentIndex++;
            }
            if (shadowMasks)
            {
                passData.gbufferRT[currentIndex] = builder.UseColorBuffer(builder.CreateTexture(
                    new TextureDesc(Vector2.one) { colorFormat = Builtin.GetShadowMaskBufferFormat(), xrInstancing = true, useDynamicScale = true, clearBuffer = clearGBuffer, clearColor = Color.clear, name = "ShadowMasks" }, HDShaderIDs._ShadowMaskTexture), currentIndex);
                currentIndex++;
            }

            output.gbuffer = new RenderGraphResource[currentIndex];
            for (int i = 0; i < currentIndex; ++i)
                output.gbuffer[i] = passData.gbufferRT[i];
        }

        // RenderGBuffer do the gbuffer pass. This is only called with deferred. If we use a depth prepass, then the depth prepass will perform the alpha testing for opaque alpha tested and we don't need to do it anymore
        // during Gbuffer pass. This is handled in the shader and the depth test (equal and no depth write) is done here.
        GBufferOutput RenderGBuffer(RenderGraph renderGraph, CullingResults cull, HDCamera hdCamera)
        {
            var output = new GBufferOutput();

            if (hdCamera.frameSettings.litShaderMode != LitShaderMode.Deferred)
                return output;

            using (var builder = renderGraph.AddRenderPass<GBufferPassData>("GBuffer", out var passData, CustomSamplerId.GBuffer.GetSampler()))
            {
                FrameSettings frameSettings = hdCamera.frameSettings;

                passData.frameSettings = frameSettings;
                SetupGBufferTargets(passData, ref output, frameSettings, builder);
                passData.rendererList = builder.UseRendererList(
                    builder.CreateRendererList(new RendererListDesc(HDShaderPassNames.s_GBufferName, cull, hdCamera.camera)
                    {
                        renderQueueRange = HDRenderQueue.k_RenderQueue_AllOpaque,
                        sortingCriteria = SortingCriteria.CommonOpaque,
                        rendererConfiguration = m_currentRendererConfigurationBakedLighting
                    }));

                builder.SetRenderFunc(
                (RenderPassData data, RenderGraphGlobalParams globalParams, RenderGraphContext renderGraphContext) =>
                {
                    GBufferPassData gbufferPassData = (GBufferPassData)data;
                    DrawOpaqueRendererList(gbufferPassData.frameSettings, renderGraphContext.resources.GetRendererList(gbufferPassData.rendererList), renderGraphContext.renderContext, renderGraphContext.cmd);
                });
            }

            return output;
        }

        class ResolvePrepassData : RenderPassData
        {
            public RenderGraphMutableResource   depthBuffer;
            public RenderGraphMutableResource   depthValuesBuffer;
            public RenderGraphMutableResource   normalBuffer;
            public RenderGraphResource          depthAsColorBufferMSAA;
            public RenderGraphResource          normalBufferMSAA;
            public Material                     depthResolveMaterial;
            public int                          depthresolvePassIndex;
        }

        RenderGraphResource ResolvePrepassBuffers(RenderGraph renderGraph, HDCamera hdCamera)
        {
            if (!hdCamera.frameSettings.IsEnabled(FrameSettingsField.MSAA))
                return new RenderGraphResource();

            using (var builder = renderGraph.AddRenderPass<ResolvePrepassData>("Resolve Prepass MSAA", out var passData))
            {
                // This texture stores a set of depth values that are required for evaluating a bunch of effects in MSAA mode (R = Samples Max Depth, G = Samples Min Depth, G =  Samples Average Depth)
                RenderGraphMutableResource depthValuesBuffer = builder.CreateTexture(new TextureDesc(Vector2.one) { colorFormat = GraphicsFormat.R32G32B32A32_SFloat, xrInstancing = true, useDynamicScale = true, name = "DepthValuesBuffer" });

                passData.depthResolveMaterial = m_DepthResolveMaterial;
                passData.depthresolvePassIndex = SampleCountToPassIndex(m_MSAASamples);

                passData.depthBuffer = builder.UseDepthBuffer(GetDepthStencilBuffer(false));
                //passData.velocityBuffer = builder.UseColorBuffer(GetVelocityBufferResource(msaa), 0);
                passData.depthValuesBuffer = builder.UseColorBuffer(depthValuesBuffer, 0);
                passData.normalBuffer = builder.UseColorBuffer(GetNormalBuffer(false), 1);

                passData.normalBufferMSAA = builder.ReadTexture(GetNormalBuffer(true));
                passData.depthAsColorBufferMSAA = builder.ReadTexture(GetDepthTexture(true));

                builder.SetRenderFunc(
                (RenderPassData data, RenderGraphGlobalParams globalParams, RenderGraphContext renderGraphContext) =>
                {
                    ResolvePrepassData resolvePrepassData = (ResolvePrepassData)data;
                    renderGraphContext.cmd.DrawProcedural(Matrix4x4.identity, resolvePrepassData.depthResolveMaterial, resolvePrepassData.depthresolvePassIndex, MeshTopology.Triangles, 3, 1);
                });

                return depthValuesBuffer;
            }
        }

        class CopyDepthPassData : RenderPassData
        {
            public RenderGraphResource          inputDepth;
            public RenderGraphMutableResource   outputDepth;
            public GPUCopy                      GPUCopy;
            public int                          width;
            public int                          height;
        }

        void CopyDepthBufferIfNeeded(RenderGraph renderGraph, HDCamera hdCamera)
        {
            if (!m_IsDepthBufferCopyValid)
            {
                using (var builder = renderGraph.AddRenderPass<CopyDepthPassData>("Copy depth buffer", out var passData, CustomSamplerId.CopyDepthBuffer.GetSampler()))
                {
                    passData.inputDepth = builder.ReadTexture(GetDepthStencilBuffer());
                    passData.outputDepth = builder.WriteTexture(GetDepthTexture());
                    passData.GPUCopy = m_GPUCopy;
                    passData.width = hdCamera.actualWidth;
                    passData.height = hdCamera.actualHeight;

                    builder.SetRenderFunc(
                    (RenderPassData data, RenderGraphGlobalParams globalParams, RenderGraphContext renderGraphContext) =>
                    {
                        CopyDepthPassData copyDepthPassData = (CopyDepthPassData)data;
                        RenderGraphResourceRegistry resources = renderGraphContext.resources;
                        // TODO: maybe we don't actually need the top MIP level?
                        // That way we could avoid making the copy, and build the MIP hierarchy directly.
                        // The downside is that our SSR tracing accuracy would decrease a little bit.
                        // But since we never render SSR at full resolution, this may be acceptable.

                        // TODO: reading the depth buffer with a compute shader will cause it to decompress in place.
                        // On console, to preserve the depth test performance, we must NOT decompress the 'm_CameraDepthStencilBuffer' in place.
                        // We should call decompressDepthSurfaceToCopy() and decompress it to 'm_CameraDepthBufferMipChain'.
                        m_GPUCopy.SampleCopyChannel_xyzw2x(renderGraphContext.cmd, resources.GetTexture(copyDepthPassData.inputDepth), resources.GetTexture(copyDepthPassData.outputDepth), new RectInt(0, 0, copyDepthPassData.width, copyDepthPassData.height));
                    });
                }

                m_IsDepthBufferCopyValid = true;
            }
        }

        class GenerateDepthPyramidPassData : RenderPassData
        {
            public RenderGraphMutableResource depthTexture;
            public HDUtils.PackedMipChainInfo mipInfo;
        }

        void GenerateDepthPyramid(RenderGraph renderGraph, HDCamera hdCamera, FullScreenDebugMode debugMode)
        {
            // If the depth buffer hasn't been already copied by the decal pass, then we do the copy here.
            CopyDepthBufferIfNeeded(renderGraph, hdCamera);

            using (var builder = renderGraph.AddRenderPass<GenerateDepthPyramidPassData>("Generate Depth Buffer MIP Chain", out var passData, CustomSamplerId.DepthPyramid.GetSampler()))
            {
                passData.depthTexture = builder.WriteTexture(GetDepthTexture());
                passData.mipInfo = GetDepthBufferMipChainInfo();

                builder.SetRenderFunc(
                (RenderPassData data, RenderGraphGlobalParams globalParams, RenderGraphContext renderGraphContext) =>
                {
                    var depthPyramidData = (GenerateDepthPyramidPassData)data;
                    m_MipGenerator.RenderMinDepthPyramid(renderGraphContext.cmd, renderGraphContext.resources.GetTexture(depthPyramidData.depthTexture), depthPyramidData.mipInfo);
                });
            }

            //int mipCount = GetDepthBufferMipChainInfo().mipLevelCount;

            //float scaleX = hdCamera.actualWidth / (float)m_SharedRTManager.GetDepthTexture().rt.width;
            //float scaleY = hdCamera.actualHeight / (float)m_SharedRTManager.GetDepthTexture().rt.height;
            //m_PyramidSizeV4F.Set(hdCamera.actualWidth, hdCamera.actualHeight, 1f / hdCamera.actualWidth, 1f / hdCamera.actualHeight);
            //m_PyramidScaleLod.Set(scaleX, scaleY, mipCount, 0.0f);
            //m_PyramidScale.Set(scaleX, scaleY, 0f, 0f);
            //cmd.SetGlobalTexture(HDShaderIDs._DepthPyramidTexture, m_SharedRTManager.GetDepthTexture());
            //cmd.SetGlobalVector(HDShaderIDs._DepthPyramidScale, m_PyramidScaleLod);

            //PushFullScreenDebugTextureMip(hdCamera, cmd, m_SharedRTManager.GetDepthTexture(), mipCount, m_PyramidScale, debugMode);
        }
    }
}
