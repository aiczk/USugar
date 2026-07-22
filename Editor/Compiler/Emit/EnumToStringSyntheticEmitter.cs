using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>Emits value-to-name helpers for reached user-enum ToString operations.</summary>
public sealed class EnumToStringSyntheticEmitter
{
    readonly EmitContext _context;
    readonly SyntheticBridgeBuilder _bridge;

    public EnumToStringSyntheticEmitter(EmitContext context, SyntheticBridgeBuilder bridge)
    {
        _context = context;
        _bridge = bridge;
    }

    public void Emit()
    {
        var builder = _context.Builder;
        foreach (var enumType in _context.Synthetics.EnumToString)
        {
            var helperName = HandlerBase.EnumToStringHelperName(enumType);
            var underlyingType = ExternResolver.GetStorageType(new RuntimeType(enumType.EnumUnderlyingType));
            var vId = $"{helperName}__v";
            var retId = NameAllocator.RetKey(helperName);
            _context.Storage.TryDeclareVar(vId, underlyingType);
            _context.Storage.TryDeclareVar(retId, StorageTypes.String);

            var function = _context.Module.AddFunction(helperName);
            function.ParamFieldNames.Add(vId);
            function.ReturnType = StorageTypes.String;
            function.ReturnSlots.Add(new ReturnSlot(retId, StorageTypes.String));

            var previousFunction = builder.CurrentFunction;
            builder.SetFunction(function);
            var value = _bridge.Load(vId, underlyingType);
            var typeName = underlyingType.Name;
            var equality = $"{typeName}.__op_Equality__{typeName}_{typeName}__SystemBoolean";
            foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>())
            {
                if (!member.HasConstantValue) continue;
                var constant = builder.Const(
                    EmitPolicy.ParseConstValue(typeName, System.Convert.ToString(
                        member.ConstantValue, System.Globalization.CultureInfo.InvariantCulture)), underlyingType);
                var isMatch = _bridge.CallExtern(StorageTypes.Boolean, equality, value, constant);
                builder.EmitIf(isMatch,
                    _ => builder.EmitReturn(builder.Const(member.Name, StorageTypes.String)));
            }

            builder.EmitReturn(_bridge.CallExtern(StorageTypes.String,
                $"{typeName}.__ToString__SystemString", value));
            if (previousFunction != null) builder.SetFunction(previousFunction);
        }
    }
}
