using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using JpnStudyTool.Models.AI;

namespace JpnStudyTool.Services;

public class AIService
{
    private readonly HttpClient _httpClient;
    private const string GeminiApiEndpointTemplate = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={0}";

    private static readonly object ResponseSchema = new
    {
        type = "OBJECT",
        properties = new
        {
            fullTranslation = new
            {
                type = "OBJECT",
                properties = new
                {
                    language = new { type = "STRING" },
                    text = new { type = "STRING" }
                },
                required = new[] { "language", "text" }
            },
            tokens = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        surface = new { type = "STRING" },
                        reading = new { type = "STRING" },
                        contextualMeaning = new { type = "STRING" },
                        partOfSpeech = new { type = "STRING" },
                        baseForm = new { type = "STRING" },
                        startIndex = new { type = "INTEGER" },
                        endIndex = new { type = "INTEGER" }
                    },
                    required = new[] { "surface", "reading", "contextualMeaning", "partOfSpeech", "baseForm", "startIndex", "endIndex" }
                }
            }
        },
        required = new[] { "fullTranslation", "tokens" }
    };
    private static readonly string ResponseSchemaJson = JsonSerializer.Serialize(ResponseSchema, new JsonSerializerOptions { WriteIndented = false });


    public AIService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<AIAnalysisResult?> AnalyzeSentenceAsync(string sentence, string apiKey, string targetLanguage = "es")
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(sentence))
        {
            return null;
        }

        string apiUrl = string.Format(GeminiApiEndpointTemplate, apiKey);
        var prompt = BuildPrompt(sentence, targetLanguage);
        var requestBody = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            },
            generationConfig = new
            {
                response_mime_type = "application/json",
                response_schema = ResponseSchema
            },
        };

        try
        {
            using StringContent jsonContent = new(
               System.Text.Json.JsonSerializer.Serialize(requestBody, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
               System.Text.Encoding.UTF8,
               "application/json");

            HttpResponseMessage response = await _httpClient.PostAsync(apiUrl, jsonContent);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                var geminiResponse = JsonSerializer.Deserialize<GeminiApiResponse>(responseBody);
                if (geminiResponse?.candidates != null && geminiResponse.candidates.Length > 0 &&
                    geminiResponse.candidates[0].content?.parts != null && geminiResponse.candidates[0].content.parts.Length > 0)
                {
                    string extractedJsonContent = geminiResponse.candidates[0].content.parts[0].text ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(extractedJsonContent))
                    {
                        try
                        {
                            var analysisResult = JsonSerializer.Deserialize<AIAnalysisResult>(extractedJsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (analysisResult?.Tokens != null && analysisResult.Tokens.Any())
                            {
                                int maxEndIndex = analysisResult.Tokens.Max(t => t.EndIndex);
                                if (maxEndIndex < sentence.Length - 5)
                                {
                                    System.Diagnostics.Debug.WriteLine($"[AIService Warning] AI tokens might be truncated. MaxEndIndex: {maxEndIndex}, SentenceLength: {sentence.Length}");
                                }
                            }
                            else if (analysisResult != null)
                            {
                                System.Diagnostics.Debug.WriteLine($"[AIService Warning] AI analysis result contained no tokens.");
                            }
                            return analysisResult;
                        }
                        catch (JsonException jsonEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[AIService] Error deserializing Gemini JSON content: {jsonEx.Message}");
                            System.Diagnostics.Debug.WriteLine($"[AIService] Failing JSON Content: {extractedJsonContent}");
                            return null;
                        }
                    }
                    else { return null; }
                }
                else { return null; }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[AIService] Gemini API request failed. Status: {response.StatusCode}");
                System.Diagnostics.Debug.WriteLine($"[AIService] Response Body: {responseBody}");
                return null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[AIService] Network/Timeout error: {ex.Message}");
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AIService] Unexpected error during API call: {ex.GetType().Name} - {ex.Message}");
            return null;
        }
    }

    private string BuildPrompt(string sentence, string targetLanguage)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Analyze the following Japanese sentence precisely:");
        sb.AppendLine($"'{sentence}'");
        sb.AppendLine();
        sb.AppendLine($"Instructions:");
        sb.AppendLine($"1. Provide a full translation of the sentence into '{targetLanguage}'.");
        sb.AppendLine($"2. Tokenize the sentence into grammatical units (words, particles, punctuation).");
        sb.AppendLine($"   - DO NOT split standard conjugated forms (e.g., '弱り切った' is one token, '立ち直らせる' is one token).");
        sb.AppendLine($"   - Treat particles (like は, を, に, で, た) correctly, often as separate tokens unless part of a fixed expression.");
        sb.AppendLine($"   - Preserve ALL original characters, including punctuation (like 、 。 「 」), in the 'surface' field of the tokens.");
        sb.AppendLine($"3. For EACH token, provide:");
        sb.AppendLine($"   - 'surface': The exact text, including original punctuation.");
        sb.AppendLine($"   - 'reading': The most likely hiragana reading. For punctuation, use the surface character.");
        sb.AppendLine($"   - 'contextualMeaning': A concise definition of the token *in the specific context of this sentence*, written in '{targetLanguage}'. For punctuation, identify it (e.g., 'comma', 'period', 'opening quote').");
        sb.AppendLine($"     * If the 'surface' form is a conjugated/inflected form of a verb or adjective (different from 'baseForm'), ADDITIONALLY explain the grammatical modification (e.g., 'past tense', 'potential form', 'te-form', 'causative form') within this 'contextualMeaning'. Example for '食べた': 'Eat (past tense)'.");
        sb.AppendLine($"   - 'partOfSpeech': The grammatical part of speech (e.g., Noun, Verb-Ichidan, Adjective-i, Particle-Case, Punctuation, Symbol). Be specific.");
        sb.AppendLine($"   - 'baseForm': The dictionary/lemma form. For punctuation, use the surface character.");
        sb.AppendLine($"   - 'startIndex': The starting character index in the original sentence (0-based).");
        sb.AppendLine($"   - 'endIndex': The ending character index (exclusive) in the original sentence. Ensure spans are contiguous and cover the entire sentence.");
        sb.AppendLine();
        sb.AppendLine($"Output ONLY the valid JSON object conforming to the schema. No extra text.");

        return sb.ToString();
    }

    private class GeminiApiResponse { public Candidate[]? candidates { get; set; } }
    private class Candidate { public Content? content { get; set; } }
    private class Content { public Part[]? parts { get; set; } }
    private class Part { public string? text { get; set; } }
}