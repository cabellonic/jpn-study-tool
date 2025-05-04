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
        sb.AppendLine($"1. Provide a complete and accurate translation of the sentence into {targetLanguage}, maintaining the original tone and style as much as possible.");
        sb.AppendLine($"2. Tokenize the sentence into **meaningful functional units**. **Strongly prioritize keeping verb/adjective conjugations, compound expressions (including preceding modifying phrases), and common concluding grammatical patterns (e.g., 'なのだろう', 'かもしれない', '〜てほしい') as SINGLE tokens.** Only split at clear grammatical boundaries where components function independently (like standalone particles), unless part of a fixed phrase."); // Added more examples and emphasis
        sb.AppendLine($"3. For EACH token identified in step 2, provide ONLY the following information:");
        sb.AppendLine($"   - 'surface': The exact text of the token (including punctuation).");
        sb.AppendLine($"   - 'reading': The most likely hiragana reading **for the entire 'surface' text**. Ensure the reading covers all characters in the surface. For punctuation, use the same character.");
        sb.AppendLine($"   - 'contextualMeaning': A concise explanation in {targetLanguage} of **what this specific token means and its function/nuance in the context of the sentence**. If it's a conjugated form or grammatical pattern, explain the combined meaning (e.g., '... (probably is the reason)', '... (state of being)'). Focus on clarity over excessive grammatical jargon."); // Adjusted nuance expectation
        sb.AppendLine($"   - 'startIndex': The starting character index (0-indexed).");
        sb.AppendLine($"   - 'endIndex': The ending character index (exclusive, 0-indexed). Ensure spans are contiguous.");
        sb.AppendLine();
        sb.AppendLine($"Example format for token analysis (Pay close attention to grouping and full readings):");
        sb.AppendLine();
        sb.AppendLine($"根底から腐れ果ててしまっている [こんていからくされはててしまっている]");
        sb.AppendLine($"\"Has completely rotted away from the base (and remains so...)\". Describes the state and origin of the decay.");
        sb.AppendLine();
        sb.AppendLine($"なのだろう [なのだろう]");
        sb.AppendLine($"\"Probably is because...\" / \"I suppose it's due to...\". Concludes the sentence with an explanatory and speculative nuance.");
        sb.AppendLine();
        sb.AppendLine($"(etc.)");
        sb.AppendLine();
        sb.AppendLine($"Return ONLY a valid JSON object conforming to the following schema (note: 'partOfSpeech' and 'baseForm' are not required from you and might be null/empty in the final JSON, focus on providing the other fields accurately based on the functional tokenization):");
        sb.AppendLine($"{{ResponseSchemaJson}}");
        sb.AppendLine();
        sb.AppendLine($"Do not include any additional text.");

        return sb.ToString();
    }

    private class GeminiApiResponse { public Candidate[]? candidates { get; set; } }
    private class Candidate { public Content? content { get; set; } }
    private class Content { public Part[]? parts { get; set; } }
    private class Part { public string? text { get; set; } }
}