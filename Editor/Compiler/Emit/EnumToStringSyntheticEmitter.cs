using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>Emits value-to-name helpers for reached user-enum ToString operations.</summary>
public sealed class EnumToStringSyntheticEmitter
{
    readonly LoweringState _context;
    readonly SyntheticBridgeBuilder _bridge;
    readonly SyntheticDemandPlan _demands;

    internal EnumToStringSyntheticEmitter(LoweringState context, SyntheticBridgeBuilder bridge,
        SyntheticDemandPlan demands)
    {
        _context = context;
        _bridge = bridge;
        _demands = demands;
    }

    public void Emit()
    {
        var builder = _context.Builder;
        foreach (var enumType in _demands.EnumToStringTypes)
        {
            var helperName = LoweringServices.EnumToStringHelperName(enumType);
            var underlyingType = _context.ResolveStorageType(enumType.EnumUnderlyingType);
            var vId = $"{helperName}__v";
            var retId = NameAllocator.RetKey(helperName);
            _context.Storage.TryDeclareVar(vId, underlyingType);
            _context.Storage.TryDeclareVar(retId, StorageTypes.String);

            var function = _context.Module.AddFunction(helperName);
            _context.Methods.AddSyntheticCallable(helperName, function, null, null,
                MethodContext.CallableKind.Synthetic, new[] { vId },
                new[] { new ReturnSlot(retId, StorageTypes.String) });
            function.ParamFieldNames.Add(vId);
            function.ReturnType = StorageTypes.String;
            function.ReturnSlots.Add(new ReturnSlot(retId, StorageTypes.String));

            var previousFunction = builder.CurrentFunction;
            builder.SetFunction(function);
            var value = _bridge.Load(vId, underlyingType);
            var typeName = underlyingType.Name;
            var equality = UdonAbiKey.Binary(typeName, "op_Equality",
                typeName, typeName, "SystemBoolean");
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
                UdonAbiKey.Method(typeName, "ToString", "SystemString"), value));
            if (previousFunction != null) builder.SetFunction(previousFunction);
        }
    }
}
