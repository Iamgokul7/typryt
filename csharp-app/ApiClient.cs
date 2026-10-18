using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Typryt;

/// <summary>Talks to the Python engine on localhost. Every call fails quietly when the engine is down.</summary>
public sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;

    public ApiClient(Uri? baseAddress = null, int timeoutMilliseconds = 500)
    {
        _http = new HttpClient
        {
            BaseAddress = baseAddress ?? new Uri("http://127.0.0.1:8000/"),
            Timeout = TimeSpan.FromMilliseconds(timeoutMilliseconds),
        };
    }

    /// <summary>Returns the engine's answer, or null if the engine is unreachable or slow.</summary>
    public async Task<CorrectResponse?> CorrectAsync(string word, string sessionId, bool abbreviationsEnabled, CancellationToken ct, bool learningEnabled = true, bool autoCapitalize = true)
    {
        try
        {
            var request = new CorrectRequest
            {
                Word = word,
                SessionId = sessionId,
                AbbreviationsEnabled = abbreviationsEnabled,
                LearningEnabled = learningEnabled,
                AutoCapitalize = autoCapitalize,
            };
            using var response = await _http.PostAsJsonAsync("correct", request, JsonOptions, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<CorrectResponse>(JsonOptions, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tells the engine the user undid a correction of this word, so it is never corrected again.
    /// Failures are ignored: the undo itself has already happened on screen.
    /// </summary>
    public async Task LearnUndoAsync(string word, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("learn/undo", new LearnUndoRequest { Word = word }, JsonOptions, ct);
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }

    /// <summary>Clears the engine's context buffer for a session. Failures are ignored.</summary>
    public async Task ResetAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("reset", new ResetRequest { SessionId = sessionId }, JsonOptions, ct);
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException)
        {
        }
    }

    /// <summary>The engine's personal words and never-correct list, or null if the engine is unreachable.</summary>
    public async Task<DictionarySnapshot?> GetDictionaryAsync(CancellationToken ct)
    {
        try
        {
            return await _http.GetFromJsonAsync<DictionarySnapshot>("dictionary", JsonOptions, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Adds a personal word. Returns false if the engine refused it or cannot be reached.</summary>
    public async Task<bool> AddPersonalWordAsync(string word, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("dictionary/words", new WordRequest { Word = word }, JsonOptions, ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>Removes a personal word. Returns false if it was not found or the engine cannot be reached.</summary>
    public Task<bool> RemovePersonalWordAsync(string word, CancellationToken ct) =>
        DeleteAsync($"dictionary/words/{Uri.EscapeDataString(word)}", ct);

    /// <summary>Unblocks a word the user undid. Returns false if it was not found or the engine cannot be reached.</summary>
    public Task<bool> RemoveNeverCorrectAsync(string word, CancellationToken ct) =>
        DeleteAsync($"dictionary/never-correct/{Uri.EscapeDataString(word)}", ct);

    private async Task<bool> DeleteAsync(string path, CancellationToken ct)
    {
        try
        {
            using var response = await _http.DeleteAsync(path, ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed class WordRequest
    {
        [JsonPropertyName("word")] public string Word { get; set; } = "";
    }

    private sealed class CorrectRequest
    {
        [JsonPropertyName("word")] public string Word { get; set; } = "";
        [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
        [JsonPropertyName("abbreviations_enabled")] public bool AbbreviationsEnabled { get; set; }
        [JsonPropertyName("learning_enabled")] public bool LearningEnabled { get; set; }
        [JsonPropertyName("auto_capitalize")] public bool AutoCapitalize { get; set; }
    }

    private sealed class LearnUndoRequest
    {
        [JsonPropertyName("word")] public string Word { get; set; } = "";
    }

    private sealed class ResetRequest
    {
        [JsonPropertyName("session_id")] public string SessionId { get; set; } = "";
    }
}

/// <summary>Response from POST /correct.</summary>
public sealed class CorrectResponse
{
    [JsonPropertyName("original")] public string Original { get; set; } = "";
    [JsonPropertyName("correction")] public string Correction { get; set; } = "";
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
    [JsonPropertyName("comma_after_previous")] public bool CommaAfterPrevious { get; set; }
    [JsonPropertyName("previous_correction")] public PreviousCorrection? PreviousCorrection { get; set; }
}

/// <summary>Response from GET /dictionary: the user's learned words and the words they undid.</summary>
public sealed class DictionarySnapshot
{
    [JsonPropertyName("personal_words")] public List<string> PersonalWords { get; set; } = new();
    [JsonPropertyName("never_correct")] public List<string> NeverCorrect { get; set; } = new();
}

/// <summary>An earlier word on screen that the new word settled (its to it's, were to we're).</summary>
public sealed class PreviousCorrection
{
    [JsonPropertyName("original")] public string Original { get; set; } = "";
    [JsonPropertyName("correction")] public string Correction { get; set; } = "";
}
