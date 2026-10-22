# FilterFit

Offline fits of the package's fitted numbers, in Python 3.9+ with NumPy.
Unity ignores this folder.

```
python3 "Tools~/FilterFit/fit_down_up.py"              # check the down/up pair (seconds)
python3 "Tools~/FilterFit/fit_down_up.py" --write      # regenerate FittedDownUp.Generated.cs
python3 "Tools~/FilterFit/fit_transition.py"           # check the virtual-level fractions (~8 min)
python3 "Tools~/FilterFit/fit_transition.py" --write   # refit them in the C#
```

A check exits nonzero when the package's numbers differ from a fresh fit.
Each script's docstring describes its method.

Every number has one source:

- What a fit chooses or produces lives in `fit_down_up.py` and is written to
  `FittedDownUp.Generated.cs`.
- What the runtime defines (the binomial, layout constants, fixed taps) lives
  in the C#; `csharp.py` reads it from there.
- The virtual levels' upsample fractions live in `TransitionKernel.cs`
  (toward the wide layout) and `WideKernel.cs` (each pitch doubling);
  `fit_transition.py` reads and rewrites them.

| File | Contents |
|---|---|
| `fit_down_up.py` | Down/up pair fit (sigma 1.46 to 2.40) and its C# writer |
| `model.py` | The down/up fit's periodic 1D model: filters as matrices |
| `positive_qp.py` | Nonnegative least squares with equalities (interior point) |
| `fit_transition.py` | Virtual-level fraction search (transition ends, pitch doublings) |
| `kernels.py` | Python mirror of the runtime's coefficient builders |
| `pipeline.py` | 1D model of the pyramid, transition and wide layouts as the shaders sample them |
| `csharp.py` | Reads named constants from `Runtime/Kernels/*.cs` |

`pipeline.py` matches the package's C# model of the same passes
(`Tests/Editor/ExecutedPipeline.cs`) within 3e-9 per pixel for the pyramid
and to double precision for the transition and wide layouts. The PlayMode
tests check that model's point moments on the GPU.

Other LAPACK builds can differ in the last bits. The down/up check allows
1e-7, relative for values above 1.
