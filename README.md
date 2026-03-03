# `new string(char[])` returns empty string in WASM async state machine when method contains a throw branch

## Description

`new string(char[])` returns `""` (length 0) in Blazor WebAssembly when:

1. The method is `async Task<T>`
2. A `throw` branch exists before the `await` points (even if the branch is dead code that never executes)
3. A `char[]` local is filled across `await` points in a loop
4. `new string(result)` is called on the filled array

The array itself is **not corrupted** — `result.Length` is correct, `result[i]` values are correct, and `new string(result.ToArray())` returns the expected string. Only `new string(result)` on the original array produces `""`.

## Reproduction

Minimal repro — single static method, no external dependencies:

### SecretGenerator.cs

```csharp
namespace BugRepro;

public static class SecretGenerator
{
    public static async Task<string> GenerateAsync()
    {
        // This branch NEVER executes — but its presence triggers the bug.
        // Remove it (or change throw to return) and the bug disappears.
        if ("abc".Length == 0)
            throw new InvalidOperationException("unreachable");

        var result = new char[4];

        for (int i = 0; i < result.Length; i++)
        {
            await Task.Yield();
            result[i] = 'a';
        }

        return new string(result); // BUG: returns "" with Length=0
    }
}
```

### Pages/Index.razor

```razor
@page "/"

<h3>WASM char[] Bug Repro</h3>
<button @onclick="RunTest">Run Test</button>
<pre>@_output</pre>

@code {
    private string _output = "Click the button to run the test.";

    private async Task RunTest()
    {
        _output = "";
        for (int round = 1; round <= 10; round++)
        {
            string secret = await BugRepro.SecretGenerator.GenerateAsync();
            _output += $"Round {round}: length={secret.Length} (expected 4)\n";
            StateHasChanged();
        }
    }
}
```

### BugRepro.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly" Version="10.0.3" />
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly.DevServer" Version="10.0.3" PrivateAssets="all" />
  </ItemGroup>
</Project>
```

Standard `Program.cs`, `App.razor`, `_Imports.razor`, and `wwwroot/index.html` for a Blazor WASM app (no custom JS required).

### Steps to reproduce

1. `dotnet new blazorwasm` targeting `net10.0`
2. Add `SecretGenerator.cs` and replace `Pages/Index.razor` with the code above
3. `dotnet run`
4. Click "Run Test" in the browser

### Expected result

```
Round 1: length=4 (expected 4)
Round 2: length=4 (expected 4)
...
```

### Actual result

```
Round 1: length=0 (expected 4)
Round 2: length=0 (expected 4)
...
```

## Diagnostic evidence

To narrow down the root cause, I added diagnostics inside `GenerateAsync` immediately after the fill loop:

```csharp
string viaDirect = new string(result);
string viaCopy   = new string(result.ToArray());
// Log: result is null, result.Length, viaDirect.Length, viaCopy.Length
```

**Output (all 10 rounds identical):**

```
result is null:      False
result.Length:       4
new string(result):  len=0 ""
new string(ToArray): len=4 "aaaa"
```

The array is valid (not null, length 4, contains `'a'` in every slot). `new string(result.ToArray())` correctly returns `"aaaa"`. Only `new string(result)` on the original array returns `""`.

## Bisection results

Systematic testing to isolate the trigger:

| Condition | Bug triggers? |
|---|---|
| No `if/throw` guard at all | **No** |
| `if (false) throw` (optimized away by Roslyn) | **No** |
| `if ("abc".Length == 0) throw` (dead code, survives to IL) | **Yes** |
| `if (...) return ""` instead of `throw` | **No** |
| `throw new InvalidOperationException()` (no message) | **Yes** |
| Static method | **Yes** |
| Instance method | **Yes** |
| `char[4]` | **Yes** |
| `char[64]` | **Yes** |
| `await Task.Yield()` (no JS interop) | **Yes** |
| `await js.InvokeAsync(...)` (JS interop) | **Yes** |
| No method parameters at all | **Yes** |
| Method has parameters (object, IJSRuntime, custom class) | **Yes** |
| Multiple fill loops + Fisher-Yates shuffle | **Yes** |
| Single fill loop, no shuffle | **Yes** |

### Key conclusions

- **Trigger**: A `throw` branch that survives compilation to IL, positioned before `await` points in an async method that fills a `char[]`. The branch does not need to be reachable at runtime.
- **Not the trigger**: `static` vs instance, method parameters, array size, type of `await`, number of loops, JS interop.
- **`return` does not trigger it** — only `throw`. Suggests the issue is related to how the async state machine is laid out when a `throw` exit path exists.
- **`if (false)` does not trigger it** — Roslyn eliminates the branch entirely, so in IL there is no throw. This confirms the bug requires the throw branch to exist in the compiled IL/state machine.

## Workaround

Replace `char[]` with `List<char>` and use `new string(result.ToArray())`:

```csharp
var result = new List<char>(4);
for (int i = 0; i < 4; i++)
{
    await Task.Yield();
    result.Add('a');
}
return new string(result.ToArray()); // Works correctly
```

Alternatively, calling `.ToArray()` on the original `char[]` also works:

```csharp
return new string(result.ToArray()); // Works even with char[]
```

## Configuration

- .NET SDK: 10.0.103
- Runtime: Microsoft.NETCore.App 10.0.3
- Microsoft.AspNetCore.Components.WebAssembly: 10.0.3
- OS: Windows 10.0.26200 (x64)
- Browsers tested: Edge
- **WASM only** — not tested on server-side Blazor or native .NET

## Analysis

The `char[]` local is hoisted as a field on the compiler-generated async state machine struct. When a `throw` branch is present in the IL before the `await` points:

- The array reference is valid (not null)
- The array length is correct
- The array contents are correct (proven by `ToArray()`)
- But `new string(char[])` reads the array incorrectly, returning `""`

This suggests the WASM runtime's implementation of `new string(char[])` (likely an intrinsic or fast-path that reads the array data pointer directly) is miscalculating an offset or length when the array was a field of an async state machine struct whose layout was affected by the presence of a `throw` branch.

`new string(result.ToArray())` works because `ToArray()` allocates a fresh array on the heap that was never a state machine field, bypassing whatever layout issue affects the original.
