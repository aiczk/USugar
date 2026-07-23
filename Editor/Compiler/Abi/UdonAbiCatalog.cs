using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Immutable view of the extern surface registered by the installed Udon SDK.
/// All compiler-created extern calls must resolve through this catalog before
/// entering Core IR; an <see cref="ExternSignature"/> is only a name candidate,
/// while a <see cref="BoundExtern"/> is registry-backed ABI authority.
/// </summary>
public sealed class UdonAbiCatalog
{
    readonly HashSet<string> _externs;
    readonly HashSet<string> _owners;

    public UdonAbiCatalog(IEnumerable<string> externNames)
    {
        if (externNames == null) throw new ArgumentNullException(nameof(externNames));
        _externs = new HashSet<string>(
            externNames.Where(name => !string.IsNullOrWhiteSpace(name)),
            StringComparer.Ordinal);
        _owners = new HashSet<string>(
            _externs.Select(ExternResolver.ExternTypePrefix),
            StringComparer.Ordinal);
    }

    public bool Contains(ExternSignature signature) => _externs.Contains(signature.Text);
    public bool Contains(string signature) => !string.IsNullOrEmpty(signature) && _externs.Contains(signature);
    public bool HasAnyExternForType(string owner) => !string.IsNullOrEmpty(owner) && _owners.Contains(owner);

    public BoundExtern Require(ExternSignature signature)
    {
        if (!_externs.Contains(signature.Text))
            throw new NotSupportedException(
                $"Udon extern '{signature.Text}' is not registered by the installed SDK.");
        return new BoundExtern(signature);
    }

    public BoundExtern Require(string signature) => Require(new ExternSignature(signature));

    public IReadOnlyCollection<string> ExternNames => _externs;
}

/// <summary>
/// An exact extern proven to exist in a <see cref="UdonAbiCatalog"/>.
/// It deliberately has no public string constructor: production IR cannot
/// represent a merely guessed extern name.
/// </summary>
public sealed class BoundExtern : IEquatable<BoundExtern>
{
    public ExternSignature Signature { get; }
    public string Text => Signature.Text;

    internal BoundExtern(ExternSignature signature) => Signature = signature;

    public bool Equals(BoundExtern other)
        => other != null && Signature.Equals(other.Signature);
    public override bool Equals(object obj) => Equals(obj as BoundExtern);
    public override int GetHashCode() => Signature.GetHashCode();
    public override string ToString() => Text;

    public static bool operator ==(BoundExtern left, BoundExtern right)
        => ReferenceEquals(left, right) || left?.Equals(right) == true;
    public static bool operator !=(BoundExtern left, BoundExtern right) => !(left == right);
}
