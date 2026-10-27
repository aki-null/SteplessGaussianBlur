using System.Collections.Generic;
using UnityEngine;

namespace SteplessGaussianBlur.Tests
{
    /// <summary>A recorder's draws in recording order; read them before the graph executes.</summary>
    sealed class RecordedPasses
    {
        static readonly int TapsId = Shader.PropertyToID("_Taps");
        static readonly int CountId = Shader.PropertyToID("_Count");

        readonly List<MaterialPropertyBlock> blocks = new List<MaterialPropertyBlock>();
        readonly List<Texture> sourceTextures = new List<Texture>();

        internal RecordedPasses(GaussianBlur blur) => blur.PassRecorded = (block, texture) =>
        {
            blocks.Add(block);
            sourceTextures.Add(texture);
        };

        internal int Count => blocks.Count;

        internal Vector4[] Taps(int pass) => blocks[pass].GetVectorArray(TapsId);

        /// <summary>The pass's fetches per axis, as the small layouts upload it.</summary>
        internal int FetchCount(int pass) => blocks[pass].GetInteger(CountId);

        internal Texture SourceTexture(int pass) => sourceTextures[pass];
    }
}
