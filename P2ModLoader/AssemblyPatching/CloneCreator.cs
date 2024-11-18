using Mono.Cecil;
using Mono.Cecil.Cil;

namespace P2ModLoader.AssemblyPatching;

public static class CloneCreator {
    public static Instruction CloneInstruction(Instruction instruction, ModuleDefinition targetModule,
        Dictionary<VariableDefinition, VariableDefinition> variableMap,
        Dictionary<ParameterDefinition, ParameterDefinition> parameterMap,
        Dictionary<Instruction, Instruction> instructionMap,
        TypeDefinition currentType) {
        var opcode = instruction.OpCode;
        var operand = instruction.Operand;

        switch (operand) {
            case null:
                return Instruction.Create(opcode);
            case TypeReference typeRef:
                return Instruction.Create(opcode, targetModule.ImportReference(typeRef));
            // TODO: Now only comparing parameter count, might miss some overloads.
            case MethodReference methodRef:
                if (methodRef.DeclaringType.FullName != currentType.FullName)
                    return Instruction.Create(opcode, targetModule.ImportReference(methodRef));
                var resolvedMethod = currentType.Methods.FirstOrDefault(m =>
                    m.Name == methodRef.Name && m.Parameters.Count == methodRef.Parameters.Count);
                return Instruction.Create(opcode, targetModule.ImportReference(resolvedMethod ?? methodRef));
            case FieldReference fieldRef:
                if (fieldRef.DeclaringType.FullName != currentType.FullName)
                    return Instruction.Create(opcode, targetModule.ImportReference(fieldRef));
                var originalField = currentType.Fields.FirstOrDefault(f => f.Name == fieldRef.Name);
                return Instruction.Create(opcode, targetModule.ImportReference(originalField ?? fieldRef));
            case string strOperand:
                return Instruction.Create(opcode, strOperand);
            case sbyte sbyteOperand:
                return Instruction.Create(opcode, sbyteOperand);
            case byte byteOperand:
                return Instruction.Create(opcode, byteOperand);
            case int intOperand:
                return Instruction.Create(opcode, intOperand);
            case long longOperand:
                return Instruction.Create(opcode, longOperand);
            case float floatOperand:
                return Instruction.Create(opcode, floatOperand);
            case double doubleOperand:
                return Instruction.Create(opcode, doubleOperand);
            case Instruction targetInstruction:
                return Instruction.Create(opcode,
                    instructionMap.GetValueOrDefault(targetInstruction, targetInstruction));
            case Instruction[] targetInstructions:
                return Instruction.Create(opcode,
                    targetInstructions.Select(ti => instructionMap.GetValueOrDefault(ti, ti)).ToArray());
            case VariableDefinition variable:
                return Instruction.Create(opcode, variableMap[variable]);
            case ParameterDefinition parameter:
                return Instruction.Create(opcode, parameterMap[parameter]);
            case CallSite callSite: 
                var newCallSite = new CallSite(targetModule.ImportReference(callSite.ReturnType)) {
                    CallingConvention = callSite.CallingConvention,
                    HasThis = callSite.HasThis,
                    ExplicitThis = callSite.ExplicitThis
                };

                foreach (var newParam in callSite.Parameters.Select(p =>
                             new ParameterDefinition(targetModule.ImportReference(p.ParameterType)))) {
                    newCallSite.Parameters.Add(newParam);
                }

                return Instruction.Create(opcode, newCallSite);
            default:
                throw new NotSupportedException($"Unsupported operand type: {operand.GetType()}");
        }
    }

    public static TypeDefinition CloneType(TypeDefinition typeToClone, ModuleDefinition targetModule) {
        var newType = new TypeDefinition(
            typeToClone.Namespace,
            typeToClone.Name,
            typeToClone.Attributes,
            typeToClone.BaseType != null ? targetModule.ImportReference(typeToClone.BaseType) : null
        );

        foreach (var interfaceImpl in typeToClone.Interfaces) {
            newType.Interfaces.Add(
                new InterfaceImplementation(targetModule.ImportReference(interfaceImpl.InterfaceType)));
        }

        foreach (var gp in typeToClone.GenericParameters) {
            var newGp = new GenericParameter(gp.Name, newType);
            newType.GenericParameters.Add(newGp);
        }

        foreach (var newMethod in typeToClone.Methods.Select(method => CloneMethod(method, targetModule))) {
            newType.Methods.Add(newMethod);
        }

        foreach (var field in typeToClone.Fields) {
            var newField = new FieldDefinition(
                field.Name,
                field.Attributes,
                targetModule.ImportReference(field.FieldType)
            );
            newType.Fields.Add(newField);
        }

        foreach (var property in typeToClone.Properties) {
            var newProperty = new PropertyDefinition(
                property.Name,
                property.Attributes,
                targetModule.ImportReference(property.PropertyType)
            );

            if (property.GetMethod != null) {
                newProperty.GetMethod = newType.Methods.FirstOrDefault(m => m.Name == property.GetMethod.Name);
            }

            if (property.SetMethod != null) {
                newProperty.SetMethod = newType.Methods.FirstOrDefault(m => m.Name == property.SetMethod.Name);
            }

            newType.Properties.Add(newProperty);
        }

        foreach (var eventDef in typeToClone.Events) {
            var newEvent = new EventDefinition(
                eventDef.Name,
                eventDef.Attributes,
                targetModule.ImportReference(eventDef.EventType)
            );

            if (eventDef.AddMethod != null) {
                newEvent.AddMethod = newType.Methods.FirstOrDefault(m => m.Name == eventDef.AddMethod.Name);
            }

            if (eventDef.RemoveMethod != null) {
                newEvent.RemoveMethod = newType.Methods.FirstOrDefault(m => m.Name == eventDef.RemoveMethod.Name);
            }

            newType.Events.Add(newEvent);
        }

        foreach (var nestedType in typeToClone.NestedTypes) {
            var newNestedType = CloneType(nestedType, targetModule);
            newType.NestedTypes.Add(newNestedType);
        }

        return newType;
    }

    public static MethodDefinition CloneMethod(MethodDefinition methodToClone, ModuleDefinition targetModule) {
        var newMethod = new MethodDefinition(
            methodToClone.Name,
            methodToClone.Attributes,
            targetModule.ImportReference(methodToClone.ReturnType)
        );

        foreach (var gp in methodToClone.GenericParameters) {
            var newGp = new GenericParameter(gp.Name, newMethod);
            newMethod.GenericParameters.Add(newGp);
        }

        foreach (var param in methodToClone.Parameters) {
            var newParam = new ParameterDefinition(param.Name, param.Attributes,
                targetModule.ImportReference(param.ParameterType));
            newMethod.Parameters.Add(newParam);
        }

        if (methodToClone.HasBody) {
            newMethod.Body = new MethodBody(newMethod);

            var variableMap = new Dictionary<VariableDefinition, VariableDefinition>();
            foreach (var variable in methodToClone.Body.Variables) {
                var newVariable = new VariableDefinition(targetModule.ImportReference(variable.VariableType));
                newMethod.Body.Variables.Add(newVariable);
                variableMap[variable] = newVariable;
            }

            newMethod.Body.InitLocals = methodToClone.Body.InitLocals;
            newMethod.Body.MaxStackSize = methodToClone.Body.MaxStackSize;

            var ilProcessor = newMethod.Body.GetILProcessor();
            var instructionMap = new Dictionary<Instruction, Instruction>();

            var currentType = methodToClone.DeclaringType;
            foreach (var instruction in methodToClone.Body.Instructions) {
                var newInstruction = CloneInstruction(instruction, targetModule, variableMap, null, instructionMap,
                    currentType);
                instructionMap[instruction] = newInstruction;
                ilProcessor.Append(newInstruction);
            }

            foreach (var instruction in newMethod.Body.Instructions) {
                instruction.Operand = instruction.Operand switch {
                    Instruction inst when instructionMap.TryGetValue(inst, out var value) => value,
                    Instruction[] insts => insts.Select(ti => instructionMap.GetValueOrDefault(ti, ti)).ToArray(),
                    _ => instruction.Operand
                };
            }

            foreach (var handler in methodToClone.Body.ExceptionHandlers) {
                var newHandler = new ExceptionHandler(handler.HandlerType) {
                    CatchType = handler.CatchType != null ? targetModule.ImportReference(handler.CatchType) : null,
                    TryStart = instructionMap[handler.TryStart],
                    TryEnd = instructionMap[handler.TryEnd],
                    HandlerStart = instructionMap[handler.HandlerStart],
                    HandlerEnd = instructionMap[handler.HandlerEnd]
                };
                newMethod.Body.ExceptionHandlers.Add(newHandler);
            }
        }

        return newMethod;
    }
}