using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using DotnetIntelligence.Analysis;

namespace DotnetIntelligence.Adapters;

/// <summary>
/// BPMN 2.0 process definitions (contract v1.1): every "*.bpmn" file gives workflow.process (id, name, lanes),
/// workflow.node (tasks, events, gateways, sub-processes, call activities, with their lane) and workflow.flow
/// (sequence flows with a redacted condition). A sibling "{name}.ProcessMetaData.json" is attached to the process; the
/// string fields named by the caller's conventions are copied onto the node whose id they describe. DTDs are refused.
/// </summary>
public static class BpmnReader
{
    private const string Model = "http://www.omg.org/spec/BPMN/20100524/MODEL";

    private static readonly Dictionary<string, string> NodeTypes = new(StringComparer.Ordinal)
    {
        ["userTask"] = "userTask", ["serviceTask"] = "serviceTask",
        ["task"] = "task", ["scriptTask"] = "task", ["sendTask"] = "task", ["receiveTask"] = "task", ["manualTask"] = "task", ["businessRuleTask"] = "task",
        ["startEvent"] = "startEvent", ["endEvent"] = "endEvent",
        ["intermediateCatchEvent"] = "intermediateEvent", ["intermediateThrowEvent"] = "intermediateEvent", ["boundaryEvent"] = "boundaryEvent",
        ["exclusiveGateway"] = "gateway", ["parallelGateway"] = "gateway", ["inclusiveGateway"] = "gateway", ["eventBasedGateway"] = "gateway", ["complexGateway"] = "gateway",
        ["subProcess"] = "subProcess", ["transaction"] = "subProcess", ["adHocSubProcess"] = "subProcess",
        ["callActivity"] = "callActivity",
    };

    public static void Read(WorkingCopyFile file, string root, IReadOnlyDictionary<string, WorkingCopyFile> filesByPath, FileScope scope, WorkflowConventions conventions, List<AnalysisDiagnostic> diagnostics)
    {
        var full = Path.Combine(root, file.Path);
        var text = File.ReadAllText(full);
        var lines = text.Split('\n');
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Warning, "bpmn-unreadable", file.Path, DiagnosticState.Failed,
                $"'{file.Path}' is not readable BPMN XML: {ex.Message}"));
            return;
        }
        if (document.Root is not { } definitions || definitions.Name.NamespaceName != Model)
        {
            diagnostics.Add(new AnalysisDiagnostic(DiagnosticSeverity.Info, "bpmn-not-bpmn20", file.Path, DiagnosticState.Unsupported,
                $"'{file.Path}' has no BPMN 2.0 definitions root ({Model}); it is not read."));
            return;
        }
        string Evidence(XElement element)
        {
            var info = (IXmlLineInfo)element;
            var line = info.HasLineInfo() ? info.LineNumber : 1;
            var column = info.HasLineInfo() ? Math.Max(1, info.LinePosition - 1) : 1;
            return scope.Evidence(file.Path, file.BlobId, line, column, element.Name.LocalName.Length + 1, line <= lines.Length ? lines[line - 1].TrimEnd('\r') : null, "syntax");
        }

        var messages = definitions.Elements(XName.Get("message", Model)).Where(m => m.Attribute("id") is not null)
            .ToDictionary(m => (string)m.Attribute("id")!, m => (string?)m.Attribute("name"), StringComparer.Ordinal);
        var metadata = Metadata(file, root, filesByPath, scope, conventions);

        foreach (var process in definitions.Elements(XName.Get("process", Model)))
        {
            var processId = (string?)process.Attribute("id") ?? string.Empty;
            var laneOf = new Dictionary<string, string>(StringComparer.Ordinal);
            var lanes = new JsonArray();
            foreach (var lane in process.Descendants(XName.Get("lane", Model)))
            {
                var laneName = (string?)lane.Attribute("name") ?? (string?)lane.Attribute("id") ?? string.Empty;
                lanes.Add(new JsonObject { ["id"] = (string?)lane.Attribute("id"), ["name"] = (string?)lane.Attribute("name") });
                foreach (var reference in lane.Elements(XName.Get("flowNodeRef", Model)))
                {
                    laneOf[reference.Value.Trim()] = laneName;
                }
            }
            scope.Observe("workflow.process", new JsonObject
            {
                ["processId"] = processId,
                ["name"] = (string?)process.Attribute("name"),
                ["file"] = file.Path,
                ["isExecutable"] = (string?)process.Attribute("isExecutable") is { } executable ? executable == "true" : null,
                ["lanes"] = lanes,
                ["metadataFile"] = metadata?.File,
            }, [Evidence(process), .. metadata?.Evidence ?? []]);

            foreach (var element in process.Descendants().Where(e => e.Name.NamespaceName == Model && NodeTypes.ContainsKey(e.Name.LocalName)))
            {
                var nodeId = (string?)element.Attribute("id");
                if (nodeId is null)
                {
                    continue;
                }
                var fields = new JsonObject
                {
                    ["processId"] = processId,
                    ["nodeId"] = nodeId,
                    ["nodeType"] = NodeTypes[element.Name.LocalName],
                    ["element"] = element.Name.LocalName,
                    ["name"] = (string?)element.Attribute("name"),
                    ["lane"] = laneOf.GetValueOrDefault(nodeId),
                    ["parent"] = element.Parent is { } parent && parent.Name.NamespaceName == Model && parent.Name.LocalName != "process" ? (string?)parent.Attribute("id") : null,
                };
                if (element.Name.LocalName == "callActivity")
                {
                    fields["calledElement"] = (string?)element.Attribute("calledElement");
                }
                if (element.Name.LocalName == "boundaryEvent")
                {
                    fields["attachedTo"] = (string?)element.Attribute("attachedToRef");
                }
                var definition = element.Elements().FirstOrDefault(e => e.Name.NamespaceName == Model && e.Name.LocalName.EndsWith("EventDefinition", StringComparison.Ordinal));
                if (definition is not null)
                {
                    fields["eventDefinition"] = definition.Name.LocalName[..^"EventDefinition".Length];
                    if ((string?)definition.Attribute("messageRef") is { } messageRef)
                    {
                        fields["messageName"] = messages.GetValueOrDefault(messageRef) ?? messageRef;
                    }
                }
                var evidence = new List<string> { Evidence(element) };
                if (metadata is not null && metadata.Nodes.TryGetValue(nodeId, out var nodeMetadata))
                {
                    fields["metadata"] = nodeMetadata.Fields.DeepClone();
                    evidence.AddRange(nodeMetadata.Evidence);
                }
                scope.Observe("workflow.node", fields, [.. evidence]);
            }

            foreach (var flow in process.Descendants(XName.Get("sequenceFlow", Model)))
            {
                var condition = flow.Element(XName.Get("conditionExpression", Model))?.Value.Trim();
                scope.Observe("workflow.flow", new JsonObject
                {
                    ["processId"] = processId,
                    ["flowId"] = (string?)flow.Attribute("id"),
                    ["from"] = (string?)flow.Attribute("sourceRef"),
                    ["to"] = (string?)flow.Attribute("targetRef"),
                    ["name"] = (string?)flow.Attribute("name"),
                    ["condition"] = string.IsNullOrEmpty(condition) ? null : Truncate(scope.Graph.Redact(condition), 300),
                }, Evidence(flow));
            }
        }
    }

    private sealed record NodeMetadata(JsonObject Fields, List<string> Evidence);

    private sealed record ProcessMetadata(string File, List<string> Evidence, Dictionary<string, NodeMetadata> Nodes);

    /// <summary>"{name}.ProcessMetaData.json" next to "{name}.bpmn": objects with an "id" equal to a node id, and the configured string fields of each.</summary>
    private static ProcessMetadata? Metadata(WorkingCopyFile bpmn, string root, IReadOnlyDictionary<string, WorkingCopyFile> filesByPath, FileScope scope, WorkflowConventions conventions)
    {
        var path = bpmn.Path[..^".bpmn".Length] + ".ProcessMetaData.json";
        if (!filesByPath.TryGetValue(path, out var file))
        {
            return null;
        }
        var bytes = File.ReadAllBytes(Path.Combine(root, file.Path));
        var keys = ConfigurationAdapter.Keys(bytes);
        string Evidence(int offset, int length)
        {
            var (line, column) = ConfigurationAdapter.Position(bytes, offset);
            return scope.Evidence(file.Path, file.BlobId, line, column, length, ConfigurationAdapter.LineAt(bytes, offset), "config");
        }
        var nodes = new Dictionary<string, NodeMetadata>(StringComparer.Ordinal);
        var byKey = keys.GroupBy(k => k.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value, offset, length) in keys)
        {
            var last = key.LastIndexOf(':');
            if (value is null || !key[(last + 1)..].Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var prefix = last < 0 ? string.Empty : key[..(last + 1)];
            var fields = new JsonObject();
            var evidence = new List<string> { Evidence(offset, length) };
            foreach (var field in conventions.Metadata.NodeFields)
            {
                if (byKey.TryGetValue(prefix + field, out var entry) && entry.Value is not null)
                {
                    fields[field] = entry.Value;
                    evidence.Add(Evidence(entry.Offset, entry.Length));
                }
            }
            nodes.TryAdd(value, new NodeMetadata(fields, evidence));
        }
        return new ProcessMetadata(file.Path, [scope.Evidence(file.Path, file.BlobId, 1, 1, 1, ConfigurationAdapter.LineAt(bytes, 0), "config")], nodes);
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
