using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.CodeGeneration;
using Microsoft.AspNetCore.Razor.Language.Intermediate;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RazorBlade.Analyzers.Support;
using SyntaxKind = Microsoft.CodeAnalysis.CSharp.SyntaxKind;

namespace RazorBlade.Analyzers;

internal class LibraryCodeGenerator
{
    private static readonly SymbolDisplayFormat _paramSignatureFormat
        = new(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType, // For enums
            parameterOptions: SymbolDisplayParameterOptions.IncludeName
                              | SymbolDisplayParameterOptions.IncludeType
                              | SymbolDisplayParameterOptions.IncludeParamsRefOut
                              | SymbolDisplayParameterOptions.IncludeDefaultValue,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                                  | SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                                  | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
        );

    private static readonly SymbolDisplayFormat _paramFootprintFormat
        = new(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            memberOptions: SymbolDisplayMemberOptions.IncludeContainingType,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
        );

    private readonly RazorCSharpDocument _generatedDoc;
    private readonly Compilation _inputCompilation;
    private readonly CSharpParseOptions _parseOptions;
    private readonly ImmutableArray<SyntaxTree> _additionalSyntaxTrees;
    private readonly CodeWriter _writer;
    private bool _hasCode;

    private INamedTypeSymbol? _classSymbol;
    private ImmutableArray<Diagnostic> _diagnostics;
    private Compilation _compilation;
    private SemanticModel? _semanticModel;
    private ClassDeclarationSyntax? _classDeclarationSyntax;

    public LibraryCodeGenerator(RazorCSharpDocument generatedDoc,
                                Compilation compilation,
                                CSharpParseOptions parseOptions,
                                ImmutableArray<SyntaxTree> additionalSyntaxTrees)
    {
        _generatedDoc = generatedDoc;
        _inputCompilation = compilation;
        _parseOptions = parseOptions;
        _additionalSyntaxTrees = additionalSyntaxTrees;

        _compilation = _inputCompilation;
        _writer = new CodeWriter(Environment.NewLine, generatedDoc.Options);
    }

    public string Generate(CancellationToken cancellationToken)
    {
        Analyze(cancellationToken);

        if (_classSymbol is not null)
        {
            _writer.WriteLine("// <auto-generated/>");
            _writer.WriteLine();

            if (!_generatedDoc.Options.SuppressNullabilityEnforcement)
            {
                _writer.WriteLine("#nullable restore");
                _writer.WriteLine();
            }

            using (_writer.BuildNamespace(_classSymbol.ContainingNamespace.ToDisplayString()))
            using (_writer.BuildClassDeclaration(["partial"], _classSymbol.Name, null, Array.Empty<string>(), Array.Empty<TypeParameter>(), useNullableContext: false))
            {
                GenerateConstructors();
                GenerateConditionalOnAsync(cancellationToken);
            }
        }

        return _hasCode ? _writer.GenerateCode() : string.Empty;
    }

    private void Analyze(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var syntaxTree = CSharpSyntaxTree.ParseText(
            _generatedDoc.GeneratedCode,
            _parseOptions,
            cancellationToken: cancellationToken
        );

        _compilation = _inputCompilation.WithOptions(_inputCompilation.Options.WithReportSuppressedDiagnostics(true))
                                        .AddSyntaxTrees(syntaxTree)
                                        .AddSyntaxTrees(_additionalSyntaxTrees);

        _semanticModel = _compilation.GetSemanticModel(syntaxTree);

        _classDeclarationSyntax = syntaxTree.GetRoot(cancellationToken)
                                            .DescendantNodes()
                                            .FirstOrDefault(static i => i.IsKind(SyntaxKind.ClassDeclaration)) as ClassDeclarationSyntax;

        _classSymbol = _classDeclarationSyntax is not null
            ? _semanticModel.GetDeclaredSymbol(_classDeclarationSyntax, cancellationToken)
            : null;

        _diagnostics = _semanticModel.GetDiagnostics(cancellationToken: cancellationToken);
    }

    private void GenerateConstructors()
    {
        if (_classSymbol?.BaseType is not { } baseType)
            return;

        var templateCtorAttribute = _compilation.GetTypeByMetadataName("RazorBlade.Support.TemplateConstructorAttribute");
        if (templateCtorAttribute is null)
            return;

        var templateCtors = _classSymbol.InstanceConstructors;

        foreach (var ctor in baseType.InstanceConstructors)
        {
            if (!ctor.HasAttribute(templateCtorAttribute))
                continue;

            if (!_compilation.IsSymbolAccessibleWithin(ctor, _classSymbol))
                continue;

            if (templateCtors.Any(defCtor => RoslynHelper.AreParameterTypesEqual(defCtor.Parameters, ctor.Parameters)))
                continue;

            StartMember();

            WriteInheritDoc(ctor);

            _writer.Write("public ")
                   .Write(_classSymbol!.Name);

            WriteParametersSignature(ctor);
            _writer.WriteLine();

            using (_writer.IndentScope())
            {
                _writer.Write(": base");
                WriteParametersCall(ctor);
                _writer.WriteLine();
            }

            _writer.WriteLine("{")
                   .WriteLine("}");
        }
    }

    private void GenerateConditionalOnAsync(CancellationToken cancellationToken)
    {
        const string executeAsyncMethodName = "ExecuteAsync";
        const string defineSectionMethodName = "DefineSection";

        var conditionalOnAsyncAttribute = _compilation.GetTypeByMetadataName("RazorBlade.Support.ConditionalOnAsyncAttribute");
        if (conditionalOnAsyncAttribute is null)
            return;

        var executeMethodSyntax = _classDeclarationSyntax?.ChildNodes()
                                                         .Where(m => m.IsKind(SyntaxKind.MethodDeclaration))
                                                         .OfType<MethodDeclarationSyntax>()
                                                         .FirstOrDefault(m => m.Identifier.ValueText == executeAsyncMethodName
                                                                              && m.Modifiers.Any(SyntaxKind.AsyncKeyword)
                                                                              && m.ParameterList.Parameters.Count == 0);

        if (executeMethodSyntax is null)
            return;

        var isTemplateSync = IsTemplateSync();
        var hiddenMethodSignatures = new HashSet<string>(StringComparer.Ordinal);

        foreach (var baseClass in _classSymbol.SelfAndBasesTypes().Skip(1))
        {
            foreach (var methodSymbol in baseClass.GetMembers().OfType<IMethodSymbol>())
            {
                if (methodSymbol.IsStatic
                    || methodSymbol.DeclaredAccessibility != Accessibility.Public
                    || !methodSymbol.CanBeReferencedByName)
                {
                    continue;
                }

                var attributeData = methodSymbol.GetAttribute(conditionalOnAsyncAttribute);
                if (attributeData?.ConstructorArguments.FirstOrDefault().Value is not bool shouldBeUsedOnAsync || shouldBeUsedOnAsync != isTemplateSync)
                    continue;

                if (!hiddenMethodSignatures.Add(GetMethodSignatureFootprint(methodSymbol)))
                    continue;

                StartMember();

                WriteInheritDoc(methodSymbol);

                // This currently doesn't have the intended effect, but leave it anyway :'(
                _writer.WriteLine("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");

                WriteObsoleteAttribute(
                    attributeData.NamedArguments.FirstOrDefault(i => i.Key == "Message").Value.Value as string
                    ?? $"This method should not be used on {(isTemplateSync ? "a synchronous" : "an asynchronous")} template.",
                    Diagnostics.GetDiagnosticId(Diagnostics.Id.ConditionalOnAsync)
                );

                _writer.Write("public new ")
                       .Write(methodSymbol.ReturnType.ToDisplayString(_paramSignatureFormat))
                       .Write(" ")
                       .Write(methodSymbol.Name.EscapeCSharpKeyword());

                WriteGenericParameters(methodSymbol);
                WriteParametersSignature(methodSymbol);
                _writer.WriteLine();

                using (_writer.IndentScope())
                {
                    _writer.Write("=> base.").Write(methodSymbol.Name.EscapeCSharpKeyword());
                    WriteGenericParameters(methodSymbol);
                    WriteParametersCall(methodSymbol);
                    _writer.WriteLine(";");
                }
            }
        }

        bool IsTemplateSync()
        {
            // CS1998 = This async method lacks 'await' operators and will run synchronously.
            // The ExecuteAsync and all the DefineSection methods need to have this diagnostic for the template to be considered synchronous.

            var diagnosticLocations = _diagnostics.Where(i => i.Id == "CS1998").Select(i => i.Location).ToHashSet();
            if (!diagnosticLocations.Contains(executeMethodSyntax.Identifier.GetLocation()))
                return false;

            var defineSectionMethod = _classSymbol.SelfAndBasesTypes()
                                                  .SelectMany(t => t.GetMembers(defineSectionMethodName))
                                                  .OfType<IMethodSymbol>()
                                                  .FirstOrDefault(m => m.Parameters is
                                                  [
                                                      { Type.SpecialType: SpecialType.System_String },
                                                      { Type.TypeKind: TypeKind.Delegate }
                                                  ]);

            if (defineSectionMethod is null || executeMethodSyntax.Body is not { } executeMethodBody)
                return true;

            foreach (var node in executeMethodBody.DescendantNodes())
            {
                if (node is InvocationExpressionSyntax
                    {
                        ArgumentList.Arguments:
                        [
                            { Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } },
                            { Expression: ParenthesizedLambdaExpressionSyntax { AsyncKeyword.RawKind: (int)SyntaxKind.AsyncKeyword } lambda }
                        ],
                        Expression: IdentifierNameSyntax { Identifier.ValueText: defineSectionMethodName } expression
                    }
                    && !diagnosticLocations.Contains(lambda.ArrowToken.GetLocation())
                    && SymbolEqualityComparer.Default.Equals(_semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol, defineSectionMethod)
                   )
                {
                    return false;
                }
            }

            return true;
        }

        static string GetMethodSignatureFootprint(IMethodSymbol methodSymbol)
        {
            var sb = new StringBuilder();

            foreach (var parameterSymbol in methodSymbol.Parameters)
            {
                if (parameterSymbol.RefKind != RefKind.None)
                    sb.Append("ref ");

                sb.Append(parameterSymbol.Type.ToDisplayString(_paramFootprintFormat));
                sb.Append(';');
            }

            return sb.ToString();
        }

        void WriteObsoleteAttribute(string message, string diagnosticId)
        {
            var obsoleteAttributeType = _compilation.GetTypeByMetadataName("System.ObsoleteAttribute");
            if (obsoleteAttributeType is null)
                return; // Shouldn't happen

            _writer.Write("[global::System.Obsolete(");
            _writer.Write(SyntaxFactory.Literal(message).ToString());

            if (obsoleteAttributeType.MemberNames.Contains("DiagnosticId"))
                _writer.Write($@", DiagnosticId = ""{diagnosticId}""");

            _writer.WriteLine(")]");
        }
    }

    private void StartMember()
    {
        if (_hasCode)
            _writer.WriteLine();
        else
            _hasCode = true;
    }

    private void WriteInheritDoc(ISymbol symbol)
    {
        var cref = DocumentationCommentId.CreateDeclarationId(symbol.OriginalDefinition);
        if (string.IsNullOrEmpty(cref))
            return;

        // Work around https://youtrack.jetbrains.com/issue/RIDER-84751
        if (cref.IndexOf('~') is >= 0 and var index)
            cref = cref.Substring(0, index);

        _writer.WriteLine($@"/// <inheritdoc cref=""{cref}"" />");
    }

    private void WriteGenericParameters(IMethodSymbol methodSymbol)
    {
        if (!methodSymbol.IsGenericMethod)
            return;

        _writer.Write("<");

        foreach (var typeParam in methodSymbol.TypeParameters)
        {
            if (typeParam.Ordinal != 0)
                _writer.WriteParameterSeparator();

            _writer.Write(typeParam.ToDisplayString(_paramSignatureFormat));
        }

        _writer.Write(">");
    }

    private void WriteParametersSignature(IMethodSymbol methodSymbol)
    {
        _writer.Write("(");

        foreach (var param in methodSymbol.Parameters)
        {
            if (param.Ordinal != 0)
                _writer.WriteParameterSeparator();

            _writer.Write(param.ToDisplayString(_paramSignatureFormat));
        }

        _writer.Write(")");
    }

    private void WriteParametersCall(IMethodSymbol methodSymbol)
    {
        _writer.Write("(");

        foreach (var param in methodSymbol.Parameters)
        {
            if (param.Ordinal != 0)
                _writer.WriteParameterSeparator();

            _writer.Write(param.RefKind switch
            {
                RefKind.Ref => "ref ",
                RefKind.Out => "out ",
                RefKind.In  => "in ",
                _           => string.Empty
            });

            _writer.Write(param.Name.EscapeCSharpKeyword());
        }

        _writer.Write(")");
    }
}
