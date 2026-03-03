namespace BugRepro;

public record DiagnosticResult(
    bool IsNull,
    int ArrayLength,
    int DirectStringLength,
    int CopiedStringLength,
    string DirectString,
    string CopiedString);

public static class SecretGenerator
{
    public static async Task<DiagnosticResult> GenerateAsync()
    {
        if ("abc".Length == 0)
            throw new InvalidOperationException("unreachable");

        var result = new char[4];

        for (int i = 0; i < result.Length; i++)
        {
            await Task.Yield();
            result[i] = 'a';
        }

        string viaDirect = new string(result);
        string viaCopy = new string(result.ToArray());

        return new DiagnosticResult(
            IsNull: result is null,
            ArrayLength: result.Length,
            DirectStringLength: viaDirect.Length,
            CopiedStringLength: viaCopy.Length,
            DirectString: viaDirect,
            CopiedString: viaCopy);
    }
}
