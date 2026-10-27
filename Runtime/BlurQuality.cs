namespace SteplessGaussianBlur
{
    /// <summary>How <see cref="GaussianBlur"/> trades speed for accuracy.</summary>
    public enum BlurQuality
    {
        /// <summary>Fastest at every sigma.</summary>
        Fast,
        /// <summary>A full-resolution Gaussian below sigma 2.24, joining Fast by 2.4: slower, more accurate.</summary>
        High
    }
}
