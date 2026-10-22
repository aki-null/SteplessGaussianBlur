using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace SteplessGaussianBlur
{
    // Blur outside a render pass: each target keeps its latest request, and
    // the next camera records them into URP's own graph, so their
    // intermediates share URP's texture reuse.
    public sealed partial class GaussianBlur
    {
        // Stored in the target, so a blur requested every frame allocates
        // nothing. No owner means no request.
        internal struct PendingBlur
        {
            internal GaussianBlur owner;
            internal Texture sourceTexture;
            // A source resized or recreated before recording drops its blur.
            internal Vector2Int sourceSize;
            internal GraphicsFormat sourceFormat;
            internal BlurStrength strength;
        }

        // Targets with a pending blur, shared by every GaussianBlur. None
        // reads a texture that another writes, so their order is free.
        static readonly List<BlurTarget> pendingTargets = new List<BlurTarget>();
        static readonly PendingBlurPass pendingPass = new PendingBlurPass();
        static readonly Action<ScriptableRenderContext, Camera> enqueuePending = EnqueuePending;
        // Kept after the queue empties: resubscribing every frame would allocate.
        static bool subscribed;

        /// <summary>Tests: whether any target still waits for a camera.</summary>
        internal static bool AnyPendingBlur => pendingTargets.Count > 0;

        /// <summary>Blurs a texture into <paramref name="target"/> before the next camera draws.</summary>
        /// <param name="source">A 2D texture.</param>
        /// <param name="strength">Sigma in source pixels, or a <see cref="BlurStrength"/>.</param>
        /// <remarks>
        /// <para>
        /// The next base Game or Scene view camera that URP renders records the
        /// blur before its own passes, so everything it draws sees the result.
        /// Until then <see cref="BlurTarget.HasPendingBlur"/> is true.
        /// </para>
        /// <list type="bullet">
        /// <item>A later call into the same target replaces this one.</item>
        /// <item>A blur whose source is destroyed, resized or recreated is dropped.</item>
        /// <item>Until the camera records, a target cannot be both blurred into and read as a source.</item>
        /// <item>A target's texture read as a source must not be resized by RecordTo in the same frame.</item>
        /// <item>In a render pass, use <see cref="RecordTo(RenderGraph, BlurSource, BlurTarget, BlurStrength)"/>.</item>
        /// </list>
        /// </remarks>
        public void Blur(Texture source, BlurTarget target, BlurStrength strength)
        {
            ThrowIfDisposed();
            if (source == null)
                throw new ArgumentException("Source Texture is null or has been destroyed.", nameof(source));
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            target.ThrowIfDisposed();
            strength.ThrowIfInvalid(nameof(strength));
            // Caller errors throw here: inside the camera's graph they would fail its frame.
            TextureDesc input = DescribeTexture(source);
            if (input.useDynamicScaleExplicit)
                throw new ArgumentException("Blur cannot read an explicitly scaled texture; blur it in a render " +
                    "pass with WithViewport.", nameof(source));
            ValidateSource(input, target);
            if (source == target.Texture)
                throw new ArgumentException("Blur a target's texture into another target.", nameof(source));
            Vector2Int viewport = default;
            Vector2Int view = default;
            ResolveSourceSizes(input, new Vector2Int(input.width, input.height), ref viewport, ref view);

            // Sizing a target releases its storage, so no pending blur may
            // read a texture that another writes.
            foreach (BlurTarget other in pendingTargets)
            {
                if (other != target && (other.Texture == source || other.Pending.sourceTexture == target.Texture))
                    throw new InvalidOperationException(
                        "Until the next camera renders, a target cannot be both blurred into and read as a source.");
            }
            if (!target.HasPendingBlur)
                pendingTargets.Add(target);
            target.Pending = new PendingBlur
            {
                owner = this,
                sourceTexture = source,
                sourceSize = new Vector2Int(source.width, source.height),
                sourceFormat = source.graphicsFormat,
                strength = strength
            };
            if (!subscribed)
            {
                RenderPipelineManager.beginCameraRendering += enqueuePending;
                subscribed = true;
            }
        }

        // TextureHandle converts implicitly to Texture, which would compile
        // and then fail outside the graph's execution.
        /// <summary>A graph texture: in a render pass, use RecordTo.</summary>
        [Obsolete("Blur reads a Texture before the next camera. In a render pass, use RecordTo.", true)]
        public void Blur(TextureHandle source, BlurTarget target, BlurStrength strength)
            => throw new NotSupportedException();

        // Every eligible camera enqueues while blurs are pending, so a camera
        // that stops before recording (failed culling) leaves them to the next.
        // A pass that runs after they were recorded finds none.
        static void EnqueuePending(ScriptableRenderContext context, Camera camera)
        {
            if (pendingTargets.Count == 0 ||
                camera.cameraType != CameraType.Game && camera.cameraType != CameraType.SceneView)
                return;
            ScriptableRenderer renderer;
            // A camera without additional data is a base camera on the default renderer.
            if (camera.TryGetComponent(out UniversalAdditionalCameraData data))
                renderer = data.renderType == CameraRenderType.Base ? data.scriptableRenderer : null;
            else
                renderer = (GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset)?.scriptableRenderer;
            renderer?.EnqueuePass(pendingPass);
        }

        static void RecordPending(RenderGraph graph)
        {
            try
            {
                foreach (BlurTarget target in pendingTargets)
                {
                    PendingBlur pending = target.Pending;
                    if (!SourceUnchanged(pending))
                        continue;
                    try
                    {
                        pending.owner.RecordTo(graph, BlurSource.FromTexture(pending.sourceTexture), target,
                            pending.strength);
                    }
                    catch (Exception exception)
                    {
                        // Validated at the call, so only a fault reaches here.
                        Debug.LogException(exception);
                    }
                }
            }
            finally
            {
                ClearPending();
            }
        }

        static bool SourceUnchanged(PendingBlur pending)
        {
            Texture source = pending.sourceTexture;
            return source != null && !(source is RenderTexture texture && !texture.IsCreated()) &&
                source.width == pending.sourceSize.x && source.height == pending.sourceSize.y &&
                source.graphicsFormat == pending.sourceFormat;
        }

        /// <summary>Drops a disposed target's pending blur.</summary>
        internal static void CancelPending(BlurTarget target)
        {
            if (!target.HasPendingBlur)
                return;
            target.Pending = default;
            pendingTargets.Remove(target);
        }

        static void ClearPending()
        {
            foreach (BlurTarget target in pendingTargets)
                target.Pending = default;
            pendingTargets.Clear();
        }

        // Entering Play Mode without a domain reload keeps statics.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetPending()
        {
            ClearPending();
            RenderPipelineManager.beginCameraRendering -= enqueuePending;
            subscribed = false;
        }

        void DisposeTextureBlur()
        {
            for (int i = pendingTargets.Count - 1; i >= 0; i--)
            {
                if (pendingTargets[i].Pending.owner == this)
                    CancelPending(pendingTargets[i]);
            }
        }

        // Runs first in the camera's frame; the blurs write their targets,
        // which RenderGraph keeps as imported textures.
        sealed class PendingBlurPass : ScriptableRenderPass
        {
            internal PendingBlurPass() => renderPassEvent = RenderPassEvent.BeforeRendering;

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
                => RecordPending(renderGraph);
        }
    }
}
