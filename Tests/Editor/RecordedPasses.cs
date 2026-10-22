using System.Collections.Generic;
using UnityEngine;

namespace SteplessGaussianBlur.Tests
{
    /// <summary>A recorder's draws in recording order; read them before the graph executes.</summary>
    sealed class RecordedPasses
    {
        static readonly int TapsId = Shader.PropertyToID("_Taps");

        readonly List<MaterialPropertyBlock> blocks = new List<MaterialPropertyBlock>();
        readonly List<Texture> sourceTextures = new List<Texture>();

        internal RecordedPasses(GaussianBlur blur) => blur.PassRecorded = (block, texture) =>
        {
            blocks.Add(block);
            sourceTextures.Add(texture);
        };

        internal int Count => blocks.Count;

        internal Vector4[] Taps(int pass) => blocks[pass].GetVectorArray(TapsId);

        internal Texture SourceTexture(int pass) => sourceTextures[pass];
    }
}
