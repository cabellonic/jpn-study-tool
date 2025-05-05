// Services/AIService.cs
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using JpnStudyTool.Models.AI;

namespace JpnStudyTool.Services;

public class AIService
{
    private readonly HttpClient _httpClient;
    private const string GeminiApiEndpointTemplate = "https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={0}";

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

    public async Task<(AIAnalysisResult? Analysis, int TokenCount)> AnalyzeSentenceAsync(string sentence, string apiKey, string targetLanguage = "es")
    {
        int tokensUsed = 0;
        AIAnalysisResult? analysisResult = null;

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(sentence))
        {
            return (null, 0);
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

                if (geminiResponse?.usageMetadata != null)
                {
                    tokensUsed = geminiResponse.usageMetadata.totalTokenCount;
                    System.Diagnostics.Debug.WriteLine($"[AIService] Token Usage - Prompt: {geminiResponse.usageMetadata.promptTokenCount}, Completion: {geminiResponse.usageMetadata.candidatesTokenCount}, Total: {tokensUsed}");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[AIService] Token Usage metadata not found in the response.");
                }

                if (geminiResponse?.candidates != null && geminiResponse.candidates.Length > 0 &&
                    geminiResponse.candidates[0].content?.parts != null && geminiResponse.candidates[0].content.parts.Length > 0)
                {
                    string extractedJsonContent = geminiResponse.candidates[0].content.parts[0].text ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(extractedJsonContent))
                    {
                        try
                        {
                            analysisResult = JsonSerializer.Deserialize<AIAnalysisResult>(extractedJsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
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
                        }
                        catch (JsonException jsonEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[AIService] Error deserializing Gemini JSON content: {jsonEx.Message}");
                            System.Diagnostics.Debug.WriteLine($"[AIService] Failing JSON Content: {extractedJsonContent}");
                            analysisResult = null;
                        }
                    }
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[AIService] Gemini API request failed. Status: {response.StatusCode}");
                System.Diagnostics.Debug.WriteLine($"[AIService] Response Body: {responseBody}");
                analysisResult = null;
                tokensUsed = 0;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"[AIService] Network/Timeout error: {ex.Message}");
            analysisResult = null;
            tokensUsed = 0;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AIService] Unexpected error during API call: {ex.GetType().Name} - {ex.Message}");
            analysisResult = null;
            tokensUsed = 0;
        }

        return (analysisResult, tokensUsed);
    }

    private string BuildPrompt(string sentence, string targetLanguage)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Analyze the following Japanese sentence precisely:");
        sb.AppendLine($"'{sentence}'");
        sb.AppendLine();
        sb.AppendLine($"Instructions:");
        sb.AppendLine($"1. Provide a complete and accurate translation of the sentence into {targetLanguage}, maintaining the original tone and style as much as possible.");
        sb.AppendLine($"2. Tokenize the sentence into **meaningful functional units**. **Strongly prioritize keeping verb/adjective conjugations, compound expressions (including preceding modifying phrases), and common concluding grammatical patterns (e.g., 'なのだろう', 'かもしれない', '〜てほしい') as SINGLE tokens.** Only split at clear grammatical boundaries where components function independently (like standalone particles), unless part of a fixed phrase.");
        sb.AppendLine($"3. For EACH token identified in step 2, provide ONLY the following information:");
        sb.AppendLine($"   - 'surface': The exact text of the token (including punctuation).");
        sb.AppendLine($"   - 'reading': The most likely hiragana reading **for the entire 'surface' text**. Ensure the reading covers all characters in the surface. For punctuation, use the same character.");
        sb.AppendLine($"   - 'contextualMeaning': A concise explanation in {targetLanguage} of **what this specific token means and its function/nuance in the context of the sentence**. If it's a conjugated form or grammatical pattern, explain the combined meaning (e.g., '... (probably is the reason)', '... (state of being)'). Focus on clarity over excessive grammatical jargon.");
        sb.AppendLine($"   - 'partOfSpeech': A simple classification (e.g., Verb, Noun, Particle, Adjective, Adverb, Punctuation, Conjugated Form, Grammatical Pattern)."); // Simplified this slightly
        sb.AppendLine($"   - 'baseForm': The dictionary form if applicable, otherwise the surface form.");
        sb.AppendLine($"   - 'startIndex': The starting character index (0-indexed).");
        sb.AppendLine($"   - 'endIndex': The ending character index (exclusive, 0-indexed). Ensure spans are contiguous.");
        sb.AppendLine();
        sb.AppendLine($"Example format for token analysis (Pay close attention to grouping and full readings):");
        sb.AppendLine();
        sb.AppendLine($"Token 1:");
        sb.AppendLine($"surface: 根底から腐れ果ててしまっている");
        sb.AppendLine($"reading: こんていからくされはててしまっている");
        sb.AppendLine($"contextualMeaning: Has completely rotted away from the base (and remains so...). Describes the state and origin of the decay.");
        sb.AppendLine($"partOfSpeech: Conjugated Form");
        sb.AppendLine($"baseForm: 腐れ果てる");
        sb.AppendLine($"startIndex: 0");
        sb.AppendLine($"endIndex: 15");
        sb.AppendLine();
        sb.AppendLine($"Token 2:");
        sb.AppendLine($"surface: なのだろう");
        sb.AppendLine($"reading: なのだろう");
        sb.AppendLine($"contextualMeaning: \"Probably is because...\" / \"I suppose it's due to...\". Concludes the sentence with an explanatory and speculative nuance.");
        sb.AppendLine($"partOfSpeech: Grammatical Pattern");
        sb.AppendLine($"baseForm: なのだろう");
        sb.AppendLine($"startIndex: 16");
        sb.AppendLine($"endIndex: 20");
        sb.AppendLine();
        sb.AppendLine($"(etc. for all tokens including punctuation)");
        sb.AppendLine();
        sb.AppendLine($"Return ONLY a valid JSON object conforming EXACTLY to the schema defined previously.");
        sb.AppendLine($"Do not include ```json markdown or any other text outside the JSON object itself.");

        return sb.ToString();
    }

    private class GeminiApiResponse
    {
        public Candidate[]? candidates { get; set; }
        public UsageMetadata? usageMetadata { get; set; }
        public PromptFeedback? promptFeedback { get; set; }
    }

    private class Candidate
    {
        public Content? content { get; set; }
        public string? finishReason { get; set; }
        public SafetyRating[]? safetyRatings { get; set; }
    }

    private class Content { public Part[]? parts { get; set; } }
    private class Part { public string? text { get; set; } }

    public class UsageMetadata
    {
        [JsonPropertyName("promptTokenCount")]
        public int promptTokenCount { get; set; }
        [JsonPropertyName("candidatesTokenCount")]
        public int candidatesTokenCount { get; set; }
        [JsonPropertyName("totalTokenCount")]
        public int totalTokenCount { get; set; }
    }

    private class SafetyRating
    {
        public string? category { get; set; }
        public string? probability { get; set; }
        public bool? blocked { get; set; }
    }

    private class PromptFeedback
    {
        public string? blockReason { get; set; }
        public SafetyRating[]? safetyRatings { get; set; }
    }
}