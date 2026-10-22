using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace SteplessGaussianBlur.Tests
{
    public sealed class BlurTextureLifetimeTests
    {
        [Test]
        public void IdentityOfAGraphViewportReturnsThatView()
        {
            BlurSourceTests.WithGraph((blur, graph) =>
            {
                TextureHandle whole = graph.CreateTexture(Descriptor(64, 48));
                TextureHandle unchanged = blur.Record(graph, whole, 0);
                Assert.That(unchanged, Is.EqualTo(whole));
                BlurSource region = ((BlurSource)whole).WithViewport(new Vector2Int(32, 24));
                TextureHandle view = blur.Record(graph, region, 0);
                Assert.That(view, Is.Not.EqualTo(whole));
                Assert.That(graph.GetTextureDesc(view).width, Is.EqualTo(32));
                Assert.That(graph.GetTextureDesc(view).height, Is.EqualTo(24));
            });
        }

        [Test]
        public void RejectedShaderLeavesNoMaterials()
        {
            Shader wrong = Shader.Find("Hidden/InternalErrorShader");
            Assume.That(wrong, Is.Not.Null);
            int before = Resources.FindObjectsOfTypeAll<Material>().Length;
            Assert.Throws<ArgumentException>(() => new GaussianBlur(wrong));
            Assert.That(Resources.FindObjectsOfTypeAll<Material>().Length, Is.EqualTo(before));
        }

        // A target is imported once per recording and size, so RenderGraph
        // orders every pass that writes or reads it.
        [Test]
        public void TargetImportsAreScopedToGraphAndRecording()
        {
            using (var blur = CreateBlur())
            using (var commands = new CommandBuffer())
            {
                var first = new RenderGraph("First blur target graph");
                var second = new RenderGraph("Second blur target graph");
                var target = new BlurTarget(fullSize: true);
                try
                {
                    TextureHandle old = default;
                    Run(first, commands, () =>
                    {
                        old = blur.RecordTo(first, first.CreateTexture(Descriptor(17, 13)), target, 1);
                        Assert.That(target.Import(first), Is.EqualTo(old), "One handle per recording and size.");
                        Assert.Throws<ArgumentException>(() => blur.RecordTo(first, old, target, 1),
                            "The target's own texture cannot be its source.");
                    });
                    Run(second, commands, () =>
                    {
                        Assert.That(target.IsImportedAs(second, old), Is.False, "Another graph's handle.");
                        blur.RecordTo(second, second.CreateTexture(Descriptor(33, 25)), target, 1);
                    });
                    Run(first, commands, () =>
                        Assert.That(target.IsImportedAs(first, old), Is.False, "An earlier recording's handle."));
                }
                finally
                {
                    target.Dispose();
                    first.Cleanup();
                    second.Cleanup();
                }
            }
        }

        [Test]
        public void PhysicalAliasGuardRejectsDistinctWrappersForOneTexture()
        {
            var texture = new RenderTexture(2, 2, 0);
            RTHandle first = RTHandles.Alloc(texture, transferOwnership: false);
            RTHandle second = RTHandles.Alloc(texture, transferOwnership: false);
            try
            {
                Assert.Throws<ArgumentException>(() => GaussianBlur.ValidatePhysicalAlias(first.nameID, second.nameID));
                Assert.DoesNotThrow(() => GaussianBlur.ValidatePhysicalAlias(first.nameID,
                    new RenderTargetIdentifier("Distinct blur target")));
            }
            finally
            {
                first.Release();
                second.Release();
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void WarmTargetImportDoesNotAllocate()
        {
            var target = new BlurTarget();
            try
            {
                BlurSourceTests.WithGraph((blur, graph) =>
                {
                    TextureHandle expected = target.Import(graph);
                    TextureHandle result = default;
                    var sourceId = new RenderTargetIdentifier(target.Texture);
                    var targetId = new RenderTargetIdentifier("Warm distinct target");
                    GaussianBlur.ValidatePhysicalAlias(sourceId, targetId);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 256; i++)
                    {
                        result = target.Import(graph);
                        GaussianBlur.ValidatePhysicalAlias(sourceId, targetId);
                    }
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.That(result, Is.EqualTo(expected));
                    Assert.That(allocated, Is.Zero, "Warm import reuse and alias validation must not allocate.");
                });
            }
            finally
            {
                target.Dispose();
            }
        }

        // A blur requested every frame replaces the pending request in place,
        // or re-enters the queue after a camera recorded it.
        [Test]
        public void RepeatedBlurRequestsDoNotAllocate()
        {
            var source = new Texture2D(8, 6, TextureFormat.RGBAHalf, false, true);
            var target = new BlurTarget();
            try
            {
                using (var blur = CreateBlur())
                {
                    blur.Blur(source, target, 3);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 256; i++)
                    {
                        if (i % 2 == 0)
                            GaussianBlur.CancelPending(target);
                        blur.Blur(source, target, 3 + i % 4);
                    }
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.That(allocated, Is.Zero, "Replacing a pending blur must not allocate.");
                }
            }
            finally
            {
                target.Dispose();
                UnityEngine.Object.DestroyImmediate(source);
            }
        }

        static GaussianBlur CreateBlur() => new GaussianBlur(BlurSourceTests.LoadShader());

        static TextureDesc Descriptor(int width, int height) =>
            new TextureDesc(width, height) { colorFormat = GraphicsFormat.R32G32B32A32_SFloat };

        static void Run(RenderGraph graph, CommandBuffer commands, Action record)
        {
            commands.Clear();
            graph.BeginRecording(new RenderGraphParameters { commandBuffer = commands });
            try
            {
                record();
            }
            finally
            {
                // Ends the recording even after a failure, so it reports
                // instead of a cleanup error.
                graph.EndRecordingAndExecute();
                graph.EndFrame();
            }
        }
    }
}
