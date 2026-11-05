using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrystalStudio;

internal sealed record ExtractionOutput(string Fingerprint,string File,bool Reused,int Occurrences);
internal sealed record ExtractionExportResult(List<ExtractionOutput> Objects,string ReportPath);
internal sealed record ExtractionReport(int SchemaVersion, string Id, DateTime CreatedUtc, List<string> Sources,
    List<string> Warnings, List<ExtractionOutput> Outputs, List<ExtractionCandidateReport> Candidates);
internal sealed record ExtractionCandidateReport(Guid Id, string Name, string Category, string Reason, string Kind,
    bool Reviewed, bool IncludeBoundary, bool CanExport, string Fingerprint, string? TemplatePath, List<ExtractionOccurrence> Occurrences);

internal static class ObjectExtractionExport
{
    private static readonly JsonSerializerOptions json=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true};
    internal static Task<ExtractionExportResult> SaveAsync(string indexPath,ExtractionSession session,
        IReadOnlyList<ExtractionCandidate> candidates,CancellationToken token,Action<string>? fault=null)
        =>Task.Run(()=>Save(indexPath,session,candidates,token,fault),token);
    internal static ExtractionExportResult Save(string indexPath,ExtractionSession session,
        IReadOnlyList<ExtractionCandidate> candidates,CancellationToken token,Action<string>? fault=null)
    {
        token.ThrowIfCancellationRequested();indexPath=Path.GetFullPath(indexPath);
        // Cooperating extraction writers serialize; other editors are detected by the
        // byte comparison immediately before committing the replacement index.
        using var writerLock=new FileStream(indexPath+".extraction.lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        byte[] before=File.ReadAllBytes(indexPath);var root=JsonNode.Parse(before)?.AsObject()??throw new InvalidDataException("Empty catalogue.");
        var index=StampCategories.Read(indexPath);var entries=root["objects"]?.AsArray()??throw new InvalidDataException("Missing catalogue objects.");
        var chosen=candidates.Where(c=>c.CanExport).GroupBy(c=>c.Fingerprint).Select(g=>g.First() with
            {Occurrences=g.SelectMany(c=>c.Occurrences.Where(o=>c.IncludeBoundary||!o.CrossesSelection)).Distinct().ToList()}).ToList();
        if(chosen.Count==0)throw new InvalidDataException("Review at least one candidate before saving.");
        var known=new Dictionary<string,(string File,JsonObject Entry)>();
        foreach(var item in entries.OfType<JsonObject>())
        {
            token.ThrowIfCancellationRequested();string? file=item["file"]?.GetValue<string>();if(file==null)continue;
            try{var cells=CellCodec.LoadStamp(StampCategories.Resolve(index.ObjectRoot,file),token);known.TryAdd(ExtractionContent.Fingerprint(cells),(file,item));}
            catch(Exception ex) when(ex is IOException or InvalidDataException or UnauthorizedAccessException){/* A broken legacy entry is retained, not reused. */}
        }
        var created=new List<string>();var outputs=new List<ExtractionOutput>();string? reportPath=null;bool committed=false;
        try
        {
            foreach(var c in chosen)
            {
                token.ThrowIfCancellationRequested();string fingerprint=c.Fingerprint;string file;JsonObject entry;bool reused;
                if(known.TryGetValue(fingerprint,out var match)){file=match.File;entry=match.Entry;reused=true;}
                else
                {
                    file=$"Extracted/{CategoryFolder(c.Category)}/{fingerprint}.X";
                    string output=StampCategories.Resolve(index.ObjectRoot,file);reused=File.Exists(output);
                    var normalized=ExtractionContent.Normalize(c.Cells);
                    if(reused)
                    {
                        if(ExtractionContent.Fingerprint(CellCodec.LoadStamp(output,token))!=fingerprint)
                            throw new IOException("An existing object has different contents: "+output);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                        string temp=output+"."+Guid.NewGuid().ToString("N")+".tmp";
                        try
                        {
                            CellCodec.SaveStamp(temp,normalized);token.ThrowIfCancellationRequested();File.Move(temp,output);created.Add(output);
                        }
                        finally{if(File.Exists(temp))File.Delete(temp);}
                    }
                    entry=new JsonObject
                    {
                        ["id"]="extracted_"+fingerprint,["file"]=file,["name"]=c.Name,["type"]=c.Category=="Unclassified"?null:c.Category,
                        ["subtype"]=null,["regions"]=new JsonArray(),["regionGroups"]=new JsonArray(),["environments"]=new JsonArray(),
                        ["tags"]=new JsonArray("extracted"),["direction"]=null,["directionStatus"]="unknown",["preview"]=null,
                        ["previewComplete"]=false,["reviewStatus"]="reviewed",["contentFingerprint"]=fingerprint
                    };
                    // Guard ID collisions, including malicious or hand-edited catalogue entries.
                    if(entries.OfType<JsonObject>().Any(e=>e["id"]?.GetValue<string>()==entry["id"]!.GetValue<string>()))
                        throw new InvalidDataException("The extracted object ID already exists with different contents.");
                    entries.Add((JsonNode)entry);known[fingerprint]=(file,entry);
                }
                // Reusing content must still persist the reviewed display metadata.
                // Other classification fields and extension properties remain intact.
                entry["name"]=c.Name;entry["type"]=c.Category=="Unclassified"?null:c.Category;entry["reviewStatus"]="reviewed";
                var saved=entry["extractionOccurrences"] as JsonArray;
                if(entry["extractionOccurrences"]!=null&&saved==null)throw new InvalidDataException("Invalid extraction occurrence records in catalogue.");
                if(saved==null){saved=[];entry["extractionOccurrences"]=saved;}
                var occurrenceKeys=saved.OfType<JsonObject>().Select(o=>o.ToJsonString()).ToHashSet();
                foreach(var occurrence in c.Occurrences)
                {
                    var value=StudioJson.SerializeToNode(occurrence,json)!.AsObject();
                    if(occurrenceKeys.Add(value.ToJsonString()))saved.Add((JsonNode)value);
                }
                outputs.Add(new(fingerprint,file,reused,c.Occurrences.Count));fault?.Invoke("object");
            }
            token.ThrowIfCancellationRequested();
            string reports=Path.Combine(Path.GetDirectoryName(indexPath)!,"extractions");Directory.CreateDirectory(reports);
            // Session IDs never become unchecked paths or overwrite an existing report.
            string reportName=CategoryFolder(session.Id)+"-"+Guid.NewGuid().ToString("N")[..8]+".json";
            reportPath=Path.Combine(reports,reportName);
            using(var stream=new FileStream(reportPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                created.Add(reportPath);
                StudioJson.Serialize(stream,new ExtractionReport(1,session.Id,DateTime.UtcNow,session.Sources,session.Warnings,outputs,
                    candidates.Select(c=>new ExtractionCandidateReport(c.Id,c.Name,c.Category,c.Reason,c.Kind.ToString(),c.Reviewed,
                        c.IncludeBoundary,c.CanExport,c.Fingerprint,c.TemplatePath,c.Occurrences)).ToList()),json);stream.Flush(true);
            }
            fault?.Invoke("report");token.ThrowIfCancellationRequested();
            if(!File.ReadAllBytes(indexPath).AsSpan().SequenceEqual(before))throw new IOException("The catalogue changed during export. Scan again before saving.");
            fault?.Invoke("index");
            AtomicFile.Write(indexPath,s=>StudioJson.Serialize(s,root,json));committed=true;
            return new(outputs,reportPath);
        }
        finally
        {
            if(!committed)foreach(string path in created.AsEnumerable().Reverse())
                try{File.Delete(path);}catch(IOException){/* Retain the original exception. Never remove pre-existing files. */}
        }
    }
    private static string CategoryFolder(string name)
    {
        string value=new(name.Trim().Select(c=>char.IsLetterOrDigit(c)||c is '-' or '_'?c:'-').Take(64).ToArray());
        return string.IsNullOrEmpty(value)?"Unclassified":value;
    }
}
