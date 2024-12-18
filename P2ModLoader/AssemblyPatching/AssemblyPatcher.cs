using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.TypeSystem;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using Mono.Cecil;
using Mono.Cecil.Cil;
using P2ModLoader.Helper;
using LanguageVersion = Microsoft.CodeAnalysis.CSharp.LanguageVersion;
using MethodBody = Mono.Cecil.Cil.MethodBody;

namespace P2ModLoader.AssemblyPatching;

public static class AssemblyPatcher {
    public static bool PatchAssembly(string dllPath, string updatedSourcePath) {
        try {
            var dllDirectory = Path.GetDirectoryName(Path.GetFullPath(dllPath));
            var fileCopy = Path.Combine(Path.GetDirectoryName(dllPath), $"{Path.GetFileNameWithoutExtension(dllPath)}Temp.dll");
            File.Copy(dllPath, fileCopy, true);

            var references = ReferenceCollector.CollectReferences(dllDirectory, dllPath, fileCopy);

            var updatedSource = File.ReadAllText(updatedSourcePath);
            var updatedTree = CSharpSyntaxTree.ParseText(updatedSource, new CSharpParseOptions(LanguageVersion.Latest));

            var updatedRoot = updatedTree.GetRoot();
            var classDeclarations = updatedRoot.DescendantNodes().OfType<ClassDeclarationSyntax>().ToList();
            var enumDeclarations = updatedRoot.DescendantNodes().OfType<EnumDeclarationSyntax>().ToList();
            var methodDeclarations = updatedRoot.DescendantNodes().OfType<MethodDeclarationSyntax>().ToList();

            var hasClass = classDeclarations.Count != 0;
            var hasEnum = enumDeclarations.Count != 0;

            if (!hasClass && !hasEnum) {
                ErrorHandler.Handle($"No classes or enums found in the source file {updatedSourcePath}", null);
                return false;
            }

            if (classDeclarations.Concat<MemberDeclarationSyntax>(enumDeclarations).Count() > 1) {
                ErrorHandler.Handle($"The file {updatedSourcePath} contains multiple member definitions.", null);
                return false;
            }

            var decompiler = new CSharpDecompiler(dllPath, new DecompilerSettings {
                ShowXmlDocumentation = false,
                RemoveDeadCode = false,
                RemoveDeadStores = false
            });

            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(dllDirectory);

            var readerParams = new ReaderParameters {
                AssemblyResolver = resolver,
                ReadWrite = true
            };

            var backupPath = dllPath + ".backup";
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Copy(dllPath, backupPath);

            using var originalAssembly = AssemblyDefinition.ReadAssembly(backupPath, readerParams);

            if (hasEnum) {
                var enumDecl = enumDeclarations.First();
                var namespaceDecl = enumDecl.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
                var namespaceName = namespaceDecl?.Name.ToString() ?? "";
                var fullTypeName = string.IsNullOrEmpty(namespaceName)
                    ? enumDecl.Identifier.Text
                    : $"{namespaceName}.{enumDecl.Identifier.Text}";

                var originalType = originalAssembly.MainModule.GetType(fullTypeName);
                if (originalType == null) {
                    if (!AddNewType(references, namespaceDecl, enumDecl, originalAssembly, readerParams))
                        return false;

                    Logger.LogInfo($"Added new enum {fullTypeName}.");
                } else {
                    if (!EnumPatcher.UpdateEnum(originalType, enumDecl, originalAssembly))
                        return false;

                    Logger.LogInfo($"Updated enum {fullTypeName} with new members.");
                }
            }

            if (hasClass) {
                var classDecl = classDeclarations.First();
                var namespaceDecl = classDecl.Ancestors().OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
                var namespaceName = namespaceDecl?.Name.ToString() ?? "";
                var fullTypeName = string.IsNullOrEmpty(namespaceName)
                    ? classDecl.Identifier.Text
                    : $"{namespaceName}.{classDecl.Identifier.Text}";
                var methodsForClass = methodDeclarations.ToList();
                var originalType = originalAssembly.MainModule.GetType(fullTypeName);

                if (originalType == null) {
                    if (!AddNewType(references, namespaceDecl, classDecl, originalAssembly, readerParams))
                        return false;
                    Logger.LogInfo($"Added new type {fullTypeName}.");
                } else {
                    if (methodsForClass.Count != 0) {
                        if (!UpdateClassTypeWithMethods(decompiler, originalAssembly, fullTypeName, namespaceDecl,
                                classDecl, methodsForClass, updatedRoot, references, readerParams))
                            return false;
                        Logger.LogInfo($"Updated class {fullTypeName} with new/changed methods.");
                    } else {
                        Logger.LogInfo($"Class {fullTypeName} found but no methods to replace.");
                    }
                }
            }

            originalAssembly.Write(dllPath);
            Logger.LogInfo($"Successfully patched assembly at {dllPath}");
            return true;
        } catch (Exception ex) {
            ErrorHandler.Handle("Error patching assembly", ex);
            return false;
        }
    }

    private static bool AddNewType(List<MetadataReference> references, NamespaceDeclarationSyntax? namespaceDecl,
        MemberDeclarationSyntax typeDecl, AssemblyDefinition originalAssembly, ReaderParameters readerParams) {
        var compilationUnit = SyntaxFactory.CompilationUnit();
        var updatedRoot = typeDecl.SyntaxTree.GetRoot();
        var usings = ReferenceCollector.CollectAllUsings(updatedRoot);
        compilationUnit = compilationUnit.WithUsings(usings);

        if (namespaceDecl != null) {
            MemberDeclarationSyntax namespaceSyntax = SyntaxFactory.NamespaceDeclaration(namespaceDecl.Name)
                .WithMembers(SyntaxFactory.SingletonList(typeDecl));
            compilationUnit = compilationUnit.WithMembers(SyntaxFactory.SingletonList(namespaceSyntax));
        } else {
            compilationUnit = compilationUnit.WithMembers(SyntaxFactory.SingletonList(typeDecl));
        }
        
        var syntaxTree = compilationUnit.SyntaxTree;

        var compilation = CSharpCompilation.Create(
            "WorkingCopy",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithPlatform(Platform.AnyCpu)
        );

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success) {
            PrintCompilationFailure(result, syntaxTree);
            return false;
        }

        ms.Seek(0, SeekOrigin.Begin);

        using var newAssembly =
            AssemblyDefinition.ReadAssembly(new MemoryStream(ms.ToArray()), readerParams);

        string fullTypeName = GetFullTypeName(namespaceDecl, typeDecl);

        var newType = newAssembly.MainModule.GetType(fullTypeName);
        if (newType == null) {
            ErrorHandler.Handle($"Could not find type {fullTypeName} in the compiled assembly", null);
            return false;
        }

        var importedType = CloneCreator.CloneType(newType, originalAssembly.MainModule);
        originalAssembly.MainModule.Types.Add(importedType);

        return true;
    }

    private static string GetFullTypeName(NamespaceDeclarationSyntax? nsDecl, MemberDeclarationSyntax typeDecl) {
        string namespaceName = nsDecl?.Name.ToString() ?? "";
        string typeName = "";
        if (typeDecl is ClassDeclarationSyntax c)
            typeName = c.Identifier.Text;
        else if (typeDecl is EnumDeclarationSyntax e)
            typeName = e.Identifier.Text;

        return string.IsNullOrEmpty(namespaceName) ? typeName : $"{namespaceName}.{typeName}";
    }

    private static bool UpdateClassTypeWithMethods(
        CSharpDecompiler decompiler,
        AssemblyDefinition originalAssembly,
        string fullTypeName,
        NamespaceDeclarationSyntax? namespaceDecl,
        ClassDeclarationSyntax classDecl,
        List<MethodDeclarationSyntax> methodGroup,
        SyntaxNode updatedRoot,
        List<MetadataReference> references,
        ReaderParameters readerParams) {
        string decompiledSource;
        try {
            decompiledSource = decompiler.DecompileTypeAsString(new FullTypeName(fullTypeName));
        } catch (Exception ex) {
            ErrorHandler.Handle($"Failed to decompile type {fullTypeName}: {ex.Message}", null);
            return false;
        }

        var decompTree = CSharpSyntaxTree.ParseText(decompiledSource);

        var decompRoot = decompTree.GetRoot() as CompilationUnitSyntax;

        if (decompRoot == null) {
            ErrorHandler.Handle("Failed to parse decompiled source.", null);
            return false;
        }

        var mergedUsings = ReferenceCollector.MergeUsings(decompRoot, updatedRoot);

        var decompClass = FindTypeDeclaration<ClassDeclarationSyntax>(decompRoot, classDecl.Identifier.Text);

        if (decompClass == null) {
            ErrorHandler.Handle($"Failed to find class {classDecl.Identifier.Text} in source.", null);
            return false;
        }

        var rewriter = new MethodReplacer(methodGroup.ToDictionary(
            m => m.Identifier.Text,
            m => m
        ));

        var modifiedClass = rewriter.Visit(decompClass) as ClassDeclarationSyntax;
        if (modifiedClass == null) {
            ErrorHandler.Handle("Failed to modify class with new methods.", null);
            return false;
        }

        CompilationUnitSyntax mergedRoot;
        if (namespaceDecl != null) {
            var mergedNamespace = SyntaxFactory.NamespaceDeclaration(namespaceDecl.Name)
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(modifiedClass));
            mergedRoot = SyntaxFactory.CompilationUnit()
                .WithUsings(mergedUsings)
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(mergedNamespace));
        } else {
            mergedRoot = SyntaxFactory.CompilationUnit()
                .WithUsings(mergedUsings)
                .WithMembers(SyntaxFactory.SingletonList<MemberDeclarationSyntax>(modifiedClass));
        }

        var mergedSource = mergedRoot.NormalizeWhitespace().ToFullString();
        var mergedTree = CSharpSyntaxTree.ParseText(mergedSource);

        var compilation = CSharpCompilation.Create(
            Path.GetRandomFileName(),
            [mergedTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                .WithOptimizationLevel(OptimizationLevel.Release)
                .WithPlatform(Platform.AnyCpu)
        );

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success) {
            PrintCompilationFailure(result, mergedTree);
            return false;
        }

        ms.Seek(0, SeekOrigin.Begin);

        using var newAssembly = AssemblyDefinition.ReadAssembly(new MemoryStream(ms.ToArray()), readerParams);

        var newType = newAssembly.MainModule.GetType(fullTypeName);

        if (newType == null) {
            ErrorHandler.Handle($"Could not find type {fullTypeName} in the compiled assembly", null);
            return false;
        }

        var originalType = originalAssembly.MainModule.GetType(fullTypeName);

        foreach (var methodName in methodGroup.Select(m => m.Identifier.Text)) {
            var newMethod = newType.Methods.FirstOrDefault(m => m.Name == methodName);
            var originalMethod = originalType.Methods.FirstOrDefault(m => m.Name == methodName);

            if (newMethod == null) {
                Logger.LogWarning($"Could not find method {methodName} in the compiled assembly");
                continue;
            }

            if (originalMethod == null) {
                Logger.LogInfo($"Adding new method {methodName} to type {fullTypeName}");
                var importedMethod = CloneCreator.CloneMethod(newMethod, originalAssembly.MainModule);
                originalType.Methods.Add(importedMethod);
                Logger.LogInfo($"Added new method {methodName}");
            } else {
                ReplaceMethodBody(originalMethod, newMethod, originalAssembly.MainModule);
                Logger.LogInfo($"Replaced method {methodName} in type {fullTypeName}");
            }
        }

        return true;
    }

    private static T? FindTypeDeclaration<T>(SyntaxNode node, string name) where T : TypeDeclarationSyntax {
        foreach (var child in node.ChildNodes()) {
            if (child is T typed && typed.Identifier.Text == name)
                return typed;

            var result = FindTypeDeclaration<T>(child, name);
            if (result != null)
                return result;
        }

        return null;
    }

    private static void PrintCompilationFailure(EmitResult result, SyntaxTree tree) {
        Logger.LogError("Compilation failed!");
        foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)) {
            var lineSpan = diagnostic.Location.GetLineSpan();
            var sourceText = tree.GetText();
            var errorLine = lineSpan.StartLinePosition.Line < sourceText.Lines.Count
                ? sourceText.Lines[lineSpan.StartLinePosition.Line].ToString()
                : "<unknown>";

            Logger.LogInfo($"{diagnostic.Id}: {diagnostic.GetMessage()}");
            Logger.LogInfo($"Location: {lineSpan}");
            Logger.LogInfo($"Source line: {errorLine}");
            Logger.LogLineBreak();
        }
    }

    private class MethodReplacer(Dictionary<string, MethodDeclarationSyntax> methods) : CSharpSyntaxRewriter {
        public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node) {
            if (methods.TryGetValue(node.Identifier.Text, out var replacementMethod)) {
                return replacementMethod.WithModifiers(replacementMethod.Modifiers)
                    .WithAttributeLists(node.AttributeLists);
            }

            return node;
        }
    }

    private static void ReplaceMethodBody(MethodDefinition originalMethod, MethodDefinition newMethod,
        ModuleDefinition targetModule) {
        originalMethod.Body = new MethodBody(originalMethod);
        originalMethod.Attributes = newMethod.Attributes;

        var variableMap = new Dictionary<VariableDefinition, VariableDefinition>();
        foreach (var variable in newMethod.Body.Variables) {
            var newVariable = new VariableDefinition(targetModule.ImportReference(variable.VariableType));
            originalMethod.Body.Variables.Add(newVariable);
            variableMap[variable] = newVariable;
        }

        var parameterMap = new Dictionary<ParameterDefinition, ParameterDefinition>();
        for (var i = 0; i < newMethod.Parameters.Count; i++) {
            parameterMap[newMethod.Parameters[i]] = originalMethod.Parameters[i];
        }

        originalMethod.Body.InitLocals = newMethod.Body.InitLocals;
        originalMethod.Body.MaxStackSize = newMethod.Body.MaxStackSize;

        var ilProcessor = originalMethod.Body.GetILProcessor();
        var instructionMap = new Dictionary<Instruction, Instruction>();

        var currentType = originalMethod.DeclaringType;
        foreach (var instruction in newMethod.Body.Instructions) {
            var newInstruction = CloneCreator.CloneInstruction(instruction, targetModule, variableMap, parameterMap,
                instructionMap, currentType);
            instructionMap[instruction] = newInstruction;
            ilProcessor.Append(newInstruction);
        }

        foreach (var instruction in originalMethod.Body.Instructions) {
            if (instruction.Operand is Instruction targetInstruction && instructionMap.ContainsKey(targetInstruction)) {
                instruction.Operand = instructionMap[targetInstruction];
            } else if (instruction.Operand is Instruction[] targetInstructions) {
                instruction.Operand = targetInstructions
                    .Select(ti => instructionMap.GetValueOrDefault(ti, ti)).ToArray();
            }
        }

        foreach (var handler in newMethod.Body.ExceptionHandlers) {
            var newHandler = new ExceptionHandler(handler.HandlerType) {
                CatchType = handler.CatchType != null ? targetModule.ImportReference(handler.CatchType) : null,
                TryStart = instructionMap[handler.TryStart],
                TryEnd = instructionMap[handler.TryEnd],
                HandlerStart = instructionMap[handler.HandlerStart],
                HandlerEnd = instructionMap[handler.HandlerEnd]
            };
            originalMethod.Body.ExceptionHandlers.Add(newHandler);
        }

        originalMethod.CustomAttributes.Clear();
        CloneCreator.CloneAttributes(newMethod, originalMethod, targetModule);
    }
}