# Running tests

Install Unity Test Framework and list the package as testable in
`Packages/manifest.json`:

```json
"testables": ["net.aki-null.stepless-gaussian-blur"]
```

In **Window > General > Test Runner**, run `SteplessGaussianBlur.Tests`
(EditMode) and `SteplessGaussianBlur.GpuTests` (PlayMode).

Both suites need an active URP asset. GPU tests also need float render
textures, texture copies and asynchronous GPU readback. Without filterable
float32 textures (most mobile GPUs) float cases run in FP16, and the tests
of float32 range or precision are ignored; set
`STEPLESS_TEST_HALF_FALLBACK=1` to run that path on any GPU.

From the command line (close the project's Editor; no `-nographics`):

```text
-batchmode -runTests -testPlatform PlayMode
-assemblyNames SteplessGaussianBlur.GpuTests -testResults <path>
```

## What they check

**EditMode** checks kernels, every layout as the shaders sample it, input
validation and shader variants. **PlayMode** renders every layout on the
GPU and compares with a CPU reference.

The CPU references share the production coefficients, so they verify that
the GPU runs the intended filters, not that they are Gaussian; the moment
checks against independently computed Gaussian moments cover that.
Performance and point-response accuracy are in the
[benchmarks](../Documentation~/Benchmarks.md).
