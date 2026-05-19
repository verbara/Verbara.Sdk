# Verbara.Sdk.Dapper.Stubs

AOT-clean drop-in replacement for `Dapper.dll`. Mirrors Dapper 2.1.72 public API surface so consumer code compiles + the Dapper.AOT source generator can detect call sites; runtime method bodies throw `NotSupportedException` with `[RequiresDynamicCode]` + `[RequiresUnreferencedCode]` annotations so ILC trims them cleanly during Native AOT publish.

## How to use

In your storage csproj:

```diff
- <PackageReference Include="Dapper" />
+ <PackageReference Include="Verbara.Sdk.Dapper.Stubs" />
+ <PackageReference Include="Dapper.AOT" PrivateAssets="all" />
```

Add to a top-level file (e.g. `AssemblyInfo.cs`):

```csharp
using Dapper;
[module: DapperAot]
```

Add to your csproj `<PropertyGroup>`:

```xml
<InterceptorsPreviewNamespaces>$(InterceptorsPreviewNamespaces);Dapper.AOT</InterceptorsPreviewNamespaces>
```

## Why this exists

`Dapper.AOT` interceptors successfully replace consumer call sites at compile time, but `Dapper.dll` itself remains in the publish output. ILC scans `Dapper.dll` and emits ~50 fatal `IL3050`/`IL207x` diagnostics from its `DynamicMethod` + `MakeGenericType` usage — even though that code is never executed.

This package ships a parallel `Dapper.dll` with the same public API surface but AOT-clean stub bodies, so ILC sees stubs instead of the real Dapper internals. The Dapper.AOT-generated interceptors continue to win at runtime, calling into `Dapper.AOT.dll` (which is already AOT-clean by design).

See [DapperLib/DapperAOT#168](https://github.com/DapperLib/DapperAOT/issues/168) for the upstream proposal.

## License

MIT.
