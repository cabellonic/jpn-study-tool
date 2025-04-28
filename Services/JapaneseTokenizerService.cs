// Services/JapaneseTokenizerService.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using JpnStudyTool.Models;
using NMeCab;
using Windows.ApplicationModel;

namespace JpnStudyTool.Services;

public class JapaneseTokenizerService
{
    private MeCabTagger? _tagger;
    private static readonly Regex KanjiRegex = new(@"[\u4E00-\u9FAF]");

    public JapaneseTokenizerService()
    {
        InitializeTagger();
    }

    private void InitializeTagger()
    {
        try
        {
            string packagePath = Package.Current.InstalledLocation.Path;
            string dicPath = Path.Combine(packagePath, "MeCab", "dic", "ipadic");
            System.Diagnostics.Debug.WriteLine($"[Tokenizer] Attempting to find dictionary at: {dicPath}");

            if (!Directory.Exists(dicPath))
            {
                System.Diagnostics.Debug.WriteLine($"[Tokenizer] Error: MeCab dictionary directory NOT FOUND at calculated path.");
                System.Diagnostics.Debug.WriteLine($"[Tokenizer] Package Installed Location: {packagePath}");
                string mecabBase = Path.Combine(packagePath, "MeCab");
                if (Directory.Exists(mecabBase)) { System.Diagnostics.Debug.WriteLine($"[Tokenizer] Contents of {mecabBase}: {string.Join(", ", Directory.GetFileSystemEntries(mecabBase))}"); string mecabDicBase = Path.Combine(packagePath, "MeCab", "dic"); if (Directory.Exists(mecabDicBase)) { System.Diagnostics.Debug.WriteLine($"[Tokenizer] Contents of {mecabDicBase}: {string.Join(", ", Directory.GetFileSystemEntries(mecabDicBase))}"); } else { System.Diagnostics.Debug.WriteLine($"[Tokenizer] Directory NOT FOUND: {mecabDicBase}"); } } else { System.Diagnostics.Debug.WriteLine($"[Tokenizer] Directory NOT FOUND: {mecabBase}"); }
                _tagger = null;
                return;
            }

            System.Diagnostics.Debug.WriteLine($"[Tokenizer] Dictionary directory found. Initializing MeCab...");
            MeCabParam param = new MeCabParam { DicDir = dicPath };
            _tagger = MeCabTagger.Create(param);

            if (_tagger != null) { System.Diagnostics.Debug.WriteLine($"[Tokenizer] MeCab tagger initialized successfully with dictionary: {dicPath}"); }
            else { System.Diagnostics.Debug.WriteLine($"[Tokenizer] MeCabTagger.Create returned NULL even though directory exists!"); }
        }
        catch (DllNotFoundException dllEx)
        {
            System.Diagnostics.Debug.WriteLine($"[Tokenizer] DllNotFoundException: {dllEx.Message}. Ensure MeCab DLLs (libmecab.dll) are correctly included and deployed for the target architecture (x64?).");
            System.Diagnostics.Debug.WriteLine($"Stack Trace: {dllEx.StackTrace}"); _tagger = null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Tokenizer] Error initializing MeCab tagger: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack Trace: {ex.StackTrace}"); _tagger = null;
        }
    }

    public List<TokenInfo> Tokenize(string sentence)
    {
        var tokens = new List<TokenInfo>();

        if (_tagger == null) { System.Diagnostics.Debug.WriteLine("[Tokenizer] Tokenize called BUT _tagger is NULL!"); return tokens; }
        if (string.IsNullOrEmpty(sentence)) { System.Diagnostics.Debug.WriteLine("[Tokenizer] Tokenize called with empty sentence."); return tokens; }
        System.Diagnostics.Debug.WriteLine($"[Tokenizer] Attempting to tokenize (tagger is NOT null): '{sentence.Substring(0, Math.Min(30, sentence.Length))}'...");

        try
        {
            MeCabNode? node = _tagger.ParseToNode(sentence);
            if (node == null) { System.Diagnostics.Debug.WriteLine("[Tokenizer] Error: MeCab ParseToNode returned null."); return tokens; }

            node = node.Next;
            while (node != null && node.Stat != MeCabNodeStat.Eos)
            {
                string[] features = node.Feature?.Split(',') ?? Array.Empty<string>();
                string surface = node.Surface ?? string.Empty;
                string partOfSpeech = features.Length > 0 ? features[0] : "-";
                string posSubcat1 = features.Length > 1 ? features[1] : "-";
                string posSubcat2 = features.Length > 2 ? features[2] : "-";
                string conjugationForm = features.Length > 4 ? features[4] : "-";
                string conjugationType = features.Length > 5 ? features[5] : "-";
                string baseForm = features.Length > 6 ? features[6] : surface;
                string reading = features.Length > 7 ? features[7] : surface;

                if (reading == "*" || string.IsNullOrEmpty(reading)) reading = surface;
                if (baseForm == "*" || string.IsNullOrEmpty(baseForm)) baseForm = surface;

                if (posSubcat1 == "*") posSubcat1 = "-";
                if (posSubcat2 == "*") posSubcat2 = "-";
                if (conjugationForm == "*") conjugationForm = "-";
                if (conjugationType == "*") conjugationType = "-";

                tokens.Add(new TokenInfo
                {
                    Surface = surface,
                    PartOfSpeech = partOfSpeech,
                    POSSubcategory1 = posSubcat1,
                    POSSubcategory2 = posSubcat2,
                    ConjugationType = conjugationType,
                    ConjugationForm = conjugationForm,
                    BaseForm = baseForm,
                    Reading = reading,
                    HasKanji = KanjiRegex.IsMatch(surface),
                });
                node = node.Next;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Tokenizer] Error during MeCab parsing: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack Trace: {ex.StackTrace}");
        }
        System.Diagnostics.Debug.WriteLine($"[Tokenizer] Finished tokenization. Found {tokens.Count} tokens.");
        return tokens;
    }
}