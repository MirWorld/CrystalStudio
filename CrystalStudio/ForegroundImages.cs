using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrystalStudio;

// A read-only view of a cell's single optional image for shared sprite walkers.
// No collection is allocated or stored by the cell.
[JsonConverter(typeof(ForegroundImagesConverter))]
public readonly struct ForegroundImages(Front? value) : IReadOnlyList<Front>
{
    public Front? Value { get; } = value;
    public int Count => Value is null ? 0 : 1;
    public Front this[int index] => index == 0 && Value is not null ? Value : throw new ArgumentOutOfRangeException(nameof(index));
    public int IndexOf(Front value) => Value == value ? 0 : -1;
    public Enumerator GetEnumerator() => new(Value);
    IEnumerator<Front> IEnumerable<Front>.GetEnumerator() => GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public struct Enumerator(Front? value) : IEnumerator<Front>
    {
        private bool visited;
        public Front Current => value!;
        object IEnumerator.Current => Current;
        public bool MoveNext() { if (visited || value is null) return false; visited = true; return true; }
        public void Reset() => visited = false;
        public void Dispose() { }
    }
}

// Keep existing single-image editor footers readable without accepting stacks.
internal sealed class ForegroundImagesConverter : JsonConverter<ForegroundImages>
{
    public override ForegroundImages Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Invalid foreground metadata.");
        if (!reader.Read()) throw new JsonException("Incomplete foreground metadata.");
        if (reader.TokenType == JsonTokenType.EndArray) return default;
        var image = JsonSerializer.Deserialize<Front>(ref reader, options) ?? throw new JsonException("Invalid foreground image.");
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("A cell supports one foreground image.");
        return new(image);
    }
    public override void Write(Utf8JsonWriter writer, ForegroundImages value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        if (value.Value is { } image) JsonSerializer.Serialize(writer, image, options);
        writer.WriteEndArray();
    }
}
