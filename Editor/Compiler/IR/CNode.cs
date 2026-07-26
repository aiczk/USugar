using System;

/// <summary>The closed instruction vocabulary accepted by <see cref="FlatBlock"/>.</summary>
public interface IFlatInstruction { }

/// <summary>Copy a leaf value into a slot.</summary>
public sealed class CAssign : IFlatInstruction
{
    public readonly int DestSlot;
    public readonly CValue Value;

    public CAssign(int destSlot, CValue value)
    {
        DestSlot = destSlot;
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>
/// A representation-preserving copy whose source and destination storage tags differ.
/// </summary>
public sealed class CRepresentationCopy : IFlatInstruction
{
    public readonly int DestSlot;
    public readonly CLeaf Source;
    public readonly StorageType DestinationType;
    public readonly RepresentationCastKind Kind;

    public CRepresentationCopy(int destSlot, CLeaf source,
        StorageType destinationType, RepresentationCastKind kind)
    {
        DestSlot = destSlot;
        Source = source ?? throw new ArgumentNullException(nameof(source));
        DestinationType = destinationType;
        Kind = kind;
    }
}

/// <summary>Store a leaf value into a heap field.</summary>
public sealed class CStoreField : IFlatInstruction
{
    public readonly string FieldName;
    public readonly CLeaf Value;

    public CStoreField(string fieldName, CLeaf value)
    {
        FieldName = fieldName
            ?? throw new ArgumentNullException(nameof(fieldName));
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>A side-effecting extern or internal call.</summary>
public sealed class CExprStmt : IFlatInstruction
{
    public readonly CValue Expr;

    public CExprStmt(CValue expr)
        => Expr = expr ?? throw new ArgumentNullException(nameof(expr));
}

/// <summary>Load a heap field into a slot.</summary>
public sealed class CLoadField : IFlatInstruction
{
    public readonly int DestSlot;
    public readonly string FieldName;
    public readonly StorageType Type;

    public CLoadField(int destSlot, string fieldName, StorageType type)
    {
        DestSlot = destSlot;
        FieldName = fieldName
            ?? throw new ArgumentNullException(nameof(fieldName));
        Type = type;
    }
}

/// <summary>The closed terminator vocabulary of a CFG basic block.</summary>
public abstract class CTerminator
{
    public static int[] GetSuccessors(CTerminator terminator)
        => terminator switch
        {
            CJump jump => new[] { jump.TargetBlockId },
            CBranch branch => new[]
            {
                branch.TrueBlockId,
                branch.FalseBlockId,
            },
            CRet => Array.Empty<int>(),
            _ => throw new VerificationException(
                "Unknown CTerminator kind in CTerminator.GetSuccessors: "
                + $"'{terminator?.GetType().Name ?? "<null>"}'."),
        };
}

public sealed class CJump : CTerminator
{
    public readonly int TargetBlockId;

    public CJump(int targetBlockId)
        => TargetBlockId = targetBlockId;
}

public sealed class CBranch : CTerminator
{
    public readonly CLeaf Cond;
    public readonly int TrueBlockId;
    public readonly int FalseBlockId;

    public CBranch(CLeaf cond, int trueBlockId, int falseBlockId)
    {
        Cond = cond ?? throw new ArgumentNullException(nameof(cond));
        TrueBlockId = trueBlockId;
        FalseBlockId = falseBlockId;
    }
}

public sealed class CRet : CTerminator
{
    public readonly CLeaf Value;

    public CRet(CLeaf value = null) => Value = value;
}
