// Copyright ©2026 Scott Blomfield

namespace RustArchon.Diagnostics.Tests;

/// <summary>
/// The shared redaction vectors: every line must come out without its secret and with the mask. The plugin's own copy of the rules is held to the same
/// file by the plugin tests, so the two cannot drift apart unnoticed.
/// </summary>
public class RedactionVectorTests
{
    public static TheoryData<string, string> Vectors()
    {
        var data = new TheoryData<string, string>();
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Vectors", "redaction.tsv")))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var tab = line.IndexOf('\t');
            data.Add(line[..tab], line[(tab + 1)..].Replace("\\n", "\n").Replace("\\t", "\t"));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Every_shared_vector_loses_its_secret(string secret, string text)
    {
        var result = LogRedactor.Redact(text);

        Assert.DoesNotContain(secret, result);
        Assert.Contains(LogRedactor.Mask, result);
    }
}
