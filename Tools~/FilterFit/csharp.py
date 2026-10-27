"""Reads named constants from the runtime's C# sources, so the Python never copies them.

    read('SmallKernel.cs', 'DownOuterMass')           ->  0.18823529411764706
    read('SmallKernel.cs', 'BinomialDownFar')         ->  43
    read('TransitionKernel.cs', 'Quarter')            ->  [(-1.25390625, 0.125, 0, 0), ...]

A constant is a const or readonly field's `Name = value;`, or a property's
`Name => value;`. The value may be arithmetic on numbers and on other
constants of the same file; `{ ... }` lists and `new Vector4(...)` become
lists and tuples, and (float)/(double) casts are dropped. Integer division
truncates, as in C#.
"""
import ast
import re
from pathlib import Path

PACKAGE = Path(__file__).resolve().parents[2]
KERNELS = PACKAGE/'Runtime'/'Kernels'

_OPERATORS = {ast.Add: lambda a, b: a+b, ast.Sub: lambda a, b: a-b, ast.Mult: lambda a, b: a*b,
              ast.Div: lambda a, b: int(a/b) if isinstance(a, int) and isinstance(b, int) else a/b}


def read(file, name):
    text = (KERNELS/file).read_text()
    # A const or readonly field's initializer, or an expression-bodied property.
    m = re.search(r'\b(?:const|readonly)\b[^;=]*\b'+name+r'\s*=(?!=)\s*([^;]+);', text) or \
        re.search(r'\b'+name+r'\s*=>\s*([^;]+);', text)
    if m is None:
        raise KeyError(f'{name} not found in {file}')
    source = m.group(1)
    source = re.sub(r'(\d+(?:\.\d*)?)[fFdD]\b', lambda f: repr(float(f.group(1))), source)  # 5f -> 5.0
    source = ' '.join(source.split())  # expressions may span lines
    source = re.sub(r'\((?:float|double)\)', '', source)  # (float)x -> x
    source = source.replace('new Vector4', '').replace('{', '[').replace('}', ']')
    return _evaluate(ast.parse(source.strip(), mode='eval').body, file)


def _evaluate(node, file):
    if isinstance(node, ast.Constant) and isinstance(node.value, (int, float)):
        return node.value
    if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.USub):
        return -_evaluate(node.operand, file)
    if isinstance(node, ast.BinOp) and type(node.op) in _OPERATORS:
        return _OPERATORS[type(node.op)](_evaluate(node.left, file), _evaluate(node.right, file))
    if isinstance(node, ast.Name):
        return read(file, node.id)
    if isinstance(node, ast.List):
        return [_evaluate(e, file) for e in node.elts]
    if isinstance(node, ast.Tuple):
        return tuple(_evaluate(e, file) for e in node.elts)
    raise ValueError(f'cannot evaluate {ast.unparse(node)} in {file}')
