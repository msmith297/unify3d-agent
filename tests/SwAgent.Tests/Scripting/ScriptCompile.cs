using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using SwAgent.Infrastructure;
using SwAgent.Scripting;
using SwAgent.Tools;

namespace SwAgent.Tests.Scripting
{
    /// <summary>
    /// Compiles cells exactly as execute_csharp does: the add-in's own ScriptOptions (references and default imports) and
    /// ScriptGlobals as the globals type. No SolidWorks needed - nothing runs.
    /// </summary>
    internal static class ScriptCompile
    {
        private static readonly Lazy<ScriptOptions> OptionsLazy = new Lazy<ScriptOptions>(Find);

        public static ScriptOptions Options => OptionsLazy.Value;

        /// <summary>The add-in's options: a static Options/BuildOptions member of ScriptHost, else its instance's lazy field.</summary>
        private static ScriptOptions Find()
        {
            AssemblyResolver.Install();
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var t = typeof(ScriptHost);
            foreach (var p in t.GetProperties(any).Where(p => p.PropertyType == typeof(ScriptOptions) && p.GetMethod.IsStatic))
                return (ScriptOptions)p.GetValue(null);
            foreach (var m in t.GetMethods(any).Where(m => m.ReturnType == typeof(ScriptOptions) && m.IsStatic && m.GetParameters().Length == 0))
                return (ScriptOptions)m.Invoke(null, null);
            var host = new ScriptHost(new ToolRegistry());
            foreach (var f in t.GetFields(any).Where(f => f.FieldType == typeof(Lazy<ScriptOptions>)))
                return ((Lazy<ScriptOptions>)f.GetValue(host)).Value;
            throw new InvalidOperationException("ScriptHost's ScriptOptions weren't found by reflection - update ScriptCompile.Find.");
        }

        public static Script<object> Script(string code) => CSharpScript.Create(code, Options, typeof(ScriptGlobals));

        public static List<Diagnostic> Errors(string code) =>
            Script(code).Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

        /// <summary>The errors and the compilation they came from (for the advisor).</summary>
        public static (List<Diagnostic> Errors, Compilation Compilation) Compile(string code)
        {
            var s = Script(code);
            var errors = s.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            return (errors, s.GetCompilation());
        }

        public static string Format(IEnumerable<Diagnostic> errors) => string.Join("\n", errors.Select(e => e.Id + ": " + e.GetMessage()));
    }
}
