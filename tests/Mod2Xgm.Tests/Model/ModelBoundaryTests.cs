using System.Reflection;
using System.Text.RegularExpressions;
using Mod2Xgm.Model;

namespace Mod2Xgm.Tests.Model;

/// <summary>
/// Plan T3.1 acceptance, "no MOD types leak": the song model (IR) must not depend on the MOD reader, so mappers and
/// writers built on it never see MOD types (docs/architecture.md#why-an-ir).
/// </summary>
public sealed partial class ModelBoundaryTests
{
    private const string ModNamespace = "Mod2Xgm.Mod";

    /// <summary>No source file under <c>src/Model</c> names the MOD namespace, fully or relative to <c>Mod2Xgm</c>.</summary>
    [Fact]
    public void ModelSourceNeverNamesTheModNamespace()
    {
        string[] files = Directory.GetFiles(Path.Combine(RepoPaths.Root, "src", "Model"), "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        string[] offenders =
        [
            .. files.SelectMany(file => File.ReadLines(file)
                .Select((line, index) => (line, index))
                .Where(l => ModReference().IsMatch(l.line))
                .Select(l => $"{Path.GetFileName(file)}:{l.index + 1}: {l.line.Trim()}")),
        ];

        Assert.True(offenders.Length == 0, "src/Model refers to the MOD reader:\n" + string.Join("\n", offenders));
    }

    /// <summary>No model type has a field, property, constructor or method whose signature involves a MOD type.</summary>
    [Fact]
    public void ModelSignaturesNeverUseModTypes()
    {
        Type[] modelTypes = [.. typeof(Song).Assembly.GetTypes().Where(t => t.Namespace == typeof(Song).Namespace)];
        Assert.Contains(typeof(Effect), modelTypes);

        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        string[] offenders =
        [
            .. modelTypes.SelectMany(type =>
                type.GetFields(all).Select(f => (Member: $"{type.Name}.{f.Name}", Types: new[] { f.FieldType }))
                    .Concat(type.GetProperties(all).Select(p => (Member: $"{type.Name}.{p.Name}", Types: new[] { p.PropertyType })))
                    .Concat(type.GetConstructors(all).Select(c => (Member: $"{type.Name}()", Types: c.GetParameters().Select(p => p.ParameterType).ToArray())))
                    .Concat(type.GetMethods(all).Select(m => (Member: $"{type.Name}.{m.Name}", Types: m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType).ToArray())))
                    .Append((Member: $"{type.Name} : base", Types: type.BaseType is null ? Type.EmptyTypes : [type.BaseType]))
                    .Where(m => m.Types.Any(IsModType))
                    .Select(m => m.Member)),
        ];

        Assert.True(offenders.Length == 0, "Model members use MOD types: " + string.Join(", ", offenders));
    }

    private static bool IsModType(Type type) =>
        type.Namespace == ModNamespace
        || (type.HasElementType && type.GetElementType() is { } element && IsModType(element))
        || (type.IsGenericType && type.GetGenericArguments().Any(IsModType));

    // "Mod2Xgm.Mod" not followed by more of a name (so "Mod2Xgm.Model" is fine), or "Mod." used relative to Mod2Xgm.
    [GeneratedRegex(@"\bMod2Xgm\.Mod\b|(?<![\w.])Mod\.[A-Z]")]
    private static partial Regex ModReference();
}
