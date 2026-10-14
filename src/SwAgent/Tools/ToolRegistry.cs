using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using SwAgent.Infrastructure;

namespace SwAgent.Tools
{
    /// <summary>Marks a method as a tool the agent (and the UI) can call.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class AgentToolAttribute : Attribute
    {
        public AgentToolAttribute(string name, string description)
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }
        public string Description { get; }
    }

    /// <summary>Describes one input property of a tool.</summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class ToolParamAttribute : Attribute
    {
        public ToolParamAttribute(string description)
        {
            Description = description;
        }

        public string Description { get; }
        public bool Required { get; set; }
    }

    /// <summary>A tool result in Claude API content-block shape (text and image blocks).</summary>
    public sealed class ToolResult
    {
        public List<JObject> Content { get; } = new List<JObject>();
        public bool IsError { get; set; }

        public ToolResult Text(string text)
        {
            Content.Add(new JObject { ["type"] = "text", ["text"] = string.IsNullOrEmpty(text) ? "(empty)" : text });
            return this;
        }

        public ToolResult Image(string base64, string mediaType = "image/jpeg")
        {
            Content.Add(new JObject
            {
                ["type"] = "image",
                ["source"] = new JObject { ["type"] = "base64", ["media_type"] = mediaType, ["data"] = base64 },
            });
            return this;
        }

        public static ToolResult Ok(string text) => new ToolResult().Text(text);
        public static ToolResult Error(string text) => new ToolResult { IsError = true }.Text(text);

        public JObject ToJson() => new JObject { ["content"] = new JArray(Content), ["isError"] = IsError };
    }

    /// <summary>
    /// Discovers [AgentTool] methods, generates JSON schemas from their input classes, and dispatches calls.
    /// Tool methods look like:  ToolResult Name(ToolContext ctx, TInput input)  or  Task&lt;ToolResult&gt; ...
    /// </summary>
    public sealed class ToolRegistry
    {
        private sealed class Entry
        {
            public string Name;
            public string Description;
            public object Target;
            public MethodInfo Method;
            public Type InputType;
            public JObject Schema;
        }

        private static readonly SnakeCaseNamingStrategy Snake = new SnakeCaseNamingStrategy();

        private static readonly JsonSerializer InputSerializer = JsonSerializer.Create(new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver { NamingStrategy = Snake },
        });

        private readonly Dictionary<string, Entry> _tools = new Dictionary<string, Entry>(StringComparer.Ordinal);

        public void RegisterAll(object target)
        {
            foreach (var method in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = method.GetCustomAttribute<AgentToolAttribute>();
                if (attr == null) continue;
                var ps = method.GetParameters();
                if (ps.Length != 2 || ps[0].ParameterType != typeof(ToolContext))
                {
                    throw new InvalidOperationException($"Tool {attr.Name}: signature must be (ToolContext, TInput)");
                }
                _tools[attr.Name] = new Entry
                {
                    Name = attr.Name,
                    Description = attr.Description,
                    Target = target,
                    Method = method,
                    InputType = ps[1].ParameterType,
                    Schema = BuildSchema(ps[1].ParameterType),
                };
            }
        }

        public IEnumerable<string> Names => _tools.Keys;

        /// <summary>Tool definitions in Claude API shape (name, description, input_schema).</summary>
        public JArray Definitions()
        {
            return new JArray(_tools.Values.OrderBy(t => t.Name).Select(t => new JObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.Schema,
            }));
        }

        public async Task<ToolResult> InvokeAsync(string name, JObject input, ToolContext ctx)
        {
            if (!_tools.TryGetValue(name, out var entry)) return ToolResult.Error($"Unknown tool '{name}'.");

            object typedInput;
            try
            {
                typedInput = (input ?? new JObject()).ToObject(entry.InputType, InputSerializer);
                var missing = entry.InputType.GetProperties()
                    .Where(p => p.GetCustomAttribute<ToolParamAttribute>()?.Required == true)
                    .Where(p => p.GetValue(typedInput) == null || (p.GetValue(typedInput) is string s && s.Length == 0))
                    .Select(p => SnakeCase(p.Name))
                    .ToList();
                if (missing.Count > 0) return ToolResult.Error("Missing required input: " + string.Join(", ", missing));
            }
            catch (Exception ex)
            {
                return ToolResult.Error($"Invalid input for {name}: {ex.Message}");
            }

            try
            {
                var started = DateTime.UtcNow;
                var raw = entry.Method.Invoke(entry.Target, new[] { ctx, typedInput });
                ToolResult result;
                try
                {
                    result = raw is Task<ToolResult> task ? await task : (ToolResult)raw;
                }
                catch (ToolException expected)
                {
                    return ToolResult.Error(expected.Message);
                }
                Log.Info($"Tool {name} finished in {(DateTime.UtcNow - started).TotalMilliseconds:N0} ms{(result.IsError ? " (error)" : "")}");
                return result;
            }
            catch (TargetInvocationException tie) when (tie.InnerException is ToolException expected)
            {
                return ToolResult.Error(expected.Message);
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                Log.Error($"Tool {name} threw", tie.InnerException);
                return ToolResult.Error($"{tie.InnerException.GetType().Name}: {tie.InnerException.Message}");
            }
            catch (Exception ex)
            {
                Log.Error($"Tool {name} threw", ex);
                return ToolResult.Error($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static JObject BuildSchema(Type inputType)
        {
            var props = new JObject();
            var required = new JArray();
            foreach (var p in inputType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var meta = p.GetCustomAttribute<ToolParamAttribute>();
                var schema = SchemaFor(p.PropertyType);
                if (meta != null) schema["description"] = meta.Description;
                var name = SnakeCase(p.Name);
                props[name] = schema;
                if (meta?.Required == true) required.Add(name);
            }
            var result = new JObject { ["type"] = "object", ["properties"] = props };
            if (required.Count > 0) result["required"] = required;
            return result;
        }

        private static JObject SchemaFor(Type t)
        {
            t = Nullable.GetUnderlyingType(t) ?? t;
            if (t == typeof(string)) return new JObject { ["type"] = "string" };
            if (t == typeof(bool)) return new JObject { ["type"] = "boolean" };
            if (t == typeof(int) || t == typeof(long)) return new JObject { ["type"] = "integer" };
            if (t == typeof(double) || t == typeof(float)) return new JObject { ["type"] = "number" };
            if (t.IsEnum) return new JObject { ["type"] = "string", ["enum"] = new JArray(Enum.GetNames(t)) };
            if (t.IsArray) return new JObject { ["type"] = "array", ["items"] = SchemaFor(t.GetElementType()) };
            if (typeof(IEnumerable).IsAssignableFrom(t) && t.IsGenericType)
            {
                return new JObject { ["type"] = "array", ["items"] = SchemaFor(t.GetGenericArguments()[0]) };
            }
            if (t.IsClass && t != typeof(object)) return BuildSchema(t); // nested input objects (e.g. ToolFile[])
            return new JObject { ["type"] = "object" };
        }

        /// <summary>CaptureViews -> capture_views (same strategy the input binder uses).</summary>
        internal static string SnakeCase(string name) => Snake.GetPropertyName(name, false);
    }
}
