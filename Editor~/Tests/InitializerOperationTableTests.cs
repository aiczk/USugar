using System.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace USugar.Tests;

public class InitializerOperationTableTests
{
    [Fact]
    public void ConstructedGenericFieldsShareOneInitializerOperationSnapshot()
    {
        var compilation = TestHelper.BuildCompilation(@"
using UdonSharp;
public class InitializerSnapshotBox<T>
{
    public T Value = default(T);
}
public class InitializerSnapshotBehaviour : UdonSharpBehaviour
{
}", "InitializerSnapshotBehaviour", out _);
        var box = compilation.GetTypeByMetadataName(
            "InitializerSnapshotBox`1");
        var intBox = box.Construct(
            compilation.GetSpecialType(SpecialType.System_Int32));
        var stringBox = box.Construct(
            compilation.GetSpecialType(SpecialType.System_String));
        var materializeCount = 0;
        var operations = new InitializerOperationTable(
            compilation,
            syntax =>
            {
                materializeCount++;
                return compilation.GetSemanticModel(
                        syntax.SyntaxTree)
                    .GetOperation(syntax);
            });

        var intInitializer = UasmEmitter
            .EnumerateClassFieldInitializers(
                intBox, operations)
            .Single();
        var stringInitializer = UasmEmitter
            .EnumerateClassFieldInitializers(
                stringBox, operations)
            .Single();
        var repeatedIntInitializer = UasmEmitter
            .EnumerateClassFieldInitializers(
                intBox, operations)
            .Single();

        Assert.Equal(1, materializeCount);
        Assert.Same(
            intInitializer.Operation,
            stringInitializer.Operation);
        Assert.Same(
            intInitializer.Operation,
            repeatedIntInitializer.Operation);
        Assert.False(SymbolEqualityComparer.Default.Equals(
            intInitializer.Field,
            stringInitializer.Field));
        Assert.True(SymbolEqualityComparer.Default.Equals(
            intBox,
            intInitializer.Field.ContainingType));
        Assert.True(SymbolEqualityComparer.Default.Equals(
            stringBox,
            stringInitializer.Field.ContainingType));
        Assert.Equal(
            SpecialType.System_Int32,
            intInitializer.Field.Type.SpecialType);
        Assert.Equal(
            SpecialType.System_String,
            stringInitializer.Field.Type.SpecialType);
    }
}
