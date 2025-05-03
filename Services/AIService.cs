// Services/AIService.cs
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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

    public async Task<AIAnalysisResult?> AnalyzeSentenceAsync(string sentence, string apiKey, string targetLanguage = "es")
    {
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(sentence))
        {
            System.Diagnostics.Debug.WriteLine("[AIService] AnalyzeSentenceAsync called with missing API key or sentence.");
            return null;
        }

        string apiUrl = string.Format(GeminiApiEndpointTemplate, apiKey);
        System.Diagnostics.Debug.WriteLine($"[AIService] Requesting analysis from: {apiUrl.Split('?')[0]}?key=...");


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

            HttpResponseMessage response = await _httpClient.PostAsJsonAsync(apiUrl, requestBody);

            string responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine("[AIService] Received successful response from Gemini.");




                var geminiResponse = JsonSerializer.Deserialize<GeminiApiResponse>(responseBody);

                if (geminiResponse?.candidates != null && geminiResponse.candidates.Length > 0 &&
                    geminiResponse.candidates[0].content?.parts != null && geminiResponse.candidates[0].content.parts.Length > 0)
                {
                    string jsonContent = geminiResponse.candidates[0].content.parts[0].text ?? string.Empty;
                    System.Diagnostics.Debug.WriteLine("[AIService] Extracted JSON content from Gemini response.");


                    if (!string.IsNullOrWhiteSpace(jsonContent))
                    {
                        try
                        {
                            var analysisResult = JsonSerializer.Deserialize<AIAnalysisResult>(jsonContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            System.Diagnostics.Debug.WriteLine($"[AIService] Successfully deserialized AIAnalysisResult. Found {analysisResult?.Tokens?.Count ?? 0} tokens.");
                            return analysisResult;
                        }
                        catch (JsonException jsonEx)
                        {
                            System.Diagnostics.Debug.WriteLine($"[AIService] Error deserializing the JSON content from Gemini: {jsonEx.Message}");
                            System.Diagnostics.Debug.WriteLine($"[AIService] Failing JSON Content: {jsonContent}");
                            return null;
                        }
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("[AIService] Extracted JSON content was empty.");
                        return null;
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[AIService] Gemini response structure was not as expected (missing candidates/content/parts).");
                    System.Diagnostics.Debug.WriteLine($"[AIService] Failing Gemini Response Body: {responseBody}");
                    return null;
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[AIService] Gemini API request failed. Status: {response.StatusCode}");
                System.Diagnostics.Debug.WriteLine($"[AIService] Response Body: {responseBody}");

                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {

                    System.Diagnostics.Debug.WriteLine("[AIService] Suggestion: Check API Key validity or request format.");
                }
                return null;
            }
        }
        catch (HttpRequestException httpEx)
        {
            System.Diagnostics.Debug.WriteLine($"[AIService] HTTP request error: {httpEx.Message}");
            return null;
        }
        catch (TaskCanceledException cancelEx)
        {
            System.Diagnostics.Debug.WriteLine($"[AIService] Request timed out: {cancelEx.Message}");
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AIService] Unexpected error during API call: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[AIService] Stack Trace: {ex.StackTrace}");
            return null;
        }
    }

    private string BuildPrompt(string sentence, string targetLanguage)
    {

        var sb = new StringBuilder();
        sb.AppendLine($"Analyze the following Japanese sentence:");
        sb.AppendLine($"'{sentence}'");
        sb.AppendLine();
        sb.AppendLine($"Provide the following information in the specified JSON format:");
        sb.AppendLine($"- A full translation of the sentence into {targetLanguage} (e.g., Spanish is 'es', English is 'en').");
        sb.AppendLine($"- An array of tokens identified in the sentence.");
        sb.AppendLine($"For EACH token, include:");
        sb.AppendLine($"  - 'surface': The exact text of the token as it appears in the sentence.");
        sb.AppendLine($"  - 'reading': The most likely hiragana reading for the token.");
        sb.AppendLine($"  - 'contextualMeaning': A concise definition of the token *specifically in the context of this sentence* (in {targetLanguage}).");
        sb.AppendLine($"  - 'partOfSpeech': The grammatical part of speech (e.g., Noun, Verb, Adjective, Particle, etc.).");
        sb.AppendLine($"  - 'baseForm': The dictionary or lemma form of the token.");
        sb.AppendLine($"  - 'startIndex': The starting character index of the token in the original sentence (0-based).");
        sb.AppendLine($"  - 'endIndex': The ending character index (exclusive) of the token in the original sentence.");
        sb.AppendLine();
        sb.AppendLine($"Ensure the output is ONLY a valid JSON object conforming EXACTLY to the provided schema. Do not include any explanatory text before or after the JSON.");

        return sb.ToString();
    }



    private class GeminiApiResponse
    {
        public Candidate[]? candidates { get; set; }
    }

    private class Candidate
    {
        public Content? content { get; set; }
    }

    private class Content
    {
        public Part[]? parts { get; set; }
    }

    private class Part
    {
        public string? text { get; set; }
    }


}