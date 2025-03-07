using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp;

namespace TestAnalyzer
{
    /// <summary>
    /// Analyzer for checking if models with a [SerializationModel] attribute also have a required [JsonSerializable] attribute on a JsonSerializationContext implementation
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class SerializationModelAnalyzer : DiagnosticAnalyzer
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("MicrosoftCodeAnalysisReleaseTracking", "RS2008:Enable analyzer release tracking", Justification = "<Pending>")]
        private static readonly DiagnosticDescriptor _rule = new DiagnosticDescriptor(
            id: "CRY001",
            title: "Classes marked with SerializationModel also require a JsonSerializable notation on the JsonSerializationContext implementation",
            messageFormat: "Classes marked with [SerializationModel] also require a JsonSerializable notation on the JsonSerializationContext implementation",
            category: "Serialization",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(_rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(context =>
            {
                var jsonSerializableContextClass = FindJsonSerializerContextSubclassWithJsonSerializable(context.Compilation);
                if (jsonSerializableContextClass == null)
                    return;
                // Get all the JsonSerializable attribute notations on context class
                var jsonSerializableAttributes = jsonSerializableContextClass
                    .GetAttributes()
                    .Where(attr => attr.AttributeClass?.ToString() == "System.Text.Json.Serialization.JsonSerializableAttribute");
                if (jsonSerializableAttributes == null)
                    return;


                context.RegisterSyntaxNodeAction(c => AnalyzeSyntax(c, jsonSerializableAttributes), SyntaxKind.RecordDeclaration);

            });

        }

        private void AnalyzeSyntax(SyntaxNodeAnalysisContext context, System.Collections.Generic.IEnumerable<AttributeData> jsonSerializableAttributes)
        {
            var classDeclaration = (RecordDeclarationSyntax)context.Node;

            // Check if the class has the [SerializationModel] attribute
            var hasSerializationModelAttribute = classDeclaration.AttributeLists
                .Any(attrList => attrList.Attributes
                    .Any(attr => attr.Name.ToString() == "SerializationModel"));

            if (!hasSerializationModelAttribute)
                return;

            // Get the type of the class with [SerializationModel] attribute
            if (context.SemanticModel.GetDeclaredSymbol(classDeclaration) is not INamedTypeSymbol classSymbol)
                return;


            var attributeFound = false;
            foreach (var jsonSerializableAttribute in jsonSerializableAttributes)
            {
                var constructorArguments = jsonSerializableAttribute.ConstructorArguments;
                if (constructorArguments.Length > 0)
                {
                    // The first constructor argument of the [JsonSerializable] attribute should match the type of the class with [SerializationModel]
#pragma warning disable RS1024 // Symbols should be compared for equality
                    if (constructorArguments[0].Value == classSymbol)
                        attributeFound = true;
#pragma warning restore RS1024 // Symbols should be compared for equality
                }
            }

            if (!attributeFound)
            {
                var diagnostic = Diagnostic.Create(_rule, classDeclaration.GetLocation(), "TRUE");
                context.ReportDiagnostic(diagnostic);
            }
        }

        private INamedTypeSymbol? FindJsonSerializerContextSubclassWithJsonSerializable(Compilation compilation)
        {
            // Look for JsonSerializerContext in all assemblies
            var jsonSerializerContextType = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonSerializerContext");
            if (jsonSerializerContextType == null)
                return null;

            // TODO the next part is a bit sketchy, finding context based on naming convention and assuming namespace

            foreach (var typeName in compilation.Assembly.TypeNames)
            {
                // Find a type with the correct naming scheme
                if (!typeName.EndsWith("SourceGenerationContext"))
                    continue;

                var type = compilation.Assembly.GetTypeByMetadataName(compilation.Assembly.Name + ".Converters." + typeName)
                    ?? compilation.Assembly.GetTypeByMetadataName(compilation.Assembly.Name + "." + typeName);
                if (type == null)
                    continue;

                if (type.InheritsFromJsonSerializerContext(compilation) &&
                    type.GetAttributes().Any(attr => attr.AttributeClass?.ToString() == "System.Text.Json.Serialization.JsonSerializableAttribute"))
                {
                    return type;
                }
            }

            return null;
        }
    }

}

public static class SymbolExtensions
{
    public static bool InheritsFromJsonSerializerContext(this INamedTypeSymbol symbol, Compilation compilation)
    {
        // Resolve JsonSerializerContext across all referenced assemblies
        var jsonSerializerContextType = compilation.GetTypeByMetadataName("System.Text.Json.Serialization.JsonSerializerContext");

        if (jsonSerializerContextType == null)
            return false;

        // Check if the symbol or any of its base types inherit from JsonSerializerContext
        var baseType = symbol.BaseType;
        while (baseType != null)
        {
            if (SymbolEqualityComparer.Default.Equals(baseType, jsonSerializerContextType))
                return true;

            baseType = baseType.BaseType;
        }

        return false;
    }
}
