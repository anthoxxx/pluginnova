using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PNJCreator
{
    public class AiResult
    {
        public bool Success;
        public string Text;
        public string Error;
        public long InputTokens;
        public long OutputTokens;
    }

    /// <summary>
    /// Appels HTTP aux fournisseurs d'IA. Tout ici s'exécute HORS du thread principal :
    /// ne jamais toucher à l'API du jeu depuis ces méthodes.
    /// </summary>
    public static class AiClient
    {
        const string AnthropicVersion = "2023-06-01";
        const string AnthropicFallbackBeta = "server-side-fallback-2026-07-01";

        static AiClient()
        {
            // TLS 1.2 obligatoire pour les API (3072 = Tls12, absent des anciens profils .NET).
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
        }

        public static Task<AiResult> ChatAsync(string provider, ProviderSettings settings, string system, List<ChatMessage> history, int maxTokens, int timeoutSeconds)
        {
            return Task.Run(() =>
            {
                try
                {
                    return provider == AiProviders.Anthropic
                        ? ChatAnthropic(settings, system, history, maxTokens, timeoutSeconds)
                        : ChatOpenAiCompatible(provider, settings, system, history, maxTokens, timeoutSeconds);
                }
                catch (Exception e)
                {
                    return new AiResult { Error = e.Message };
                }
            });
        }

        public static Task<(List<string> models, string error)> ListModelsAsync(string provider, ProviderSettings settings)
        {
            return Task.Run(() =>
            {
                try
                {
                    var ids = new List<string>();
                    if (provider == AiProviders.Anthropic)
                    {
                        string after = null;
                        for (int page = 0; page < 10; page++)
                        {
                            string url = settings.BaseUrl + "/v1/models?limit=100" + (after != null ? "&after_id=" + Uri.EscapeDataString(after) : "");
                            var json = JObject.Parse(Http("GET", url, AnthropicHeaders(settings, false), null, 20));
                            foreach (var m in json["data"] ?? new JArray()) ids.Add((string)m["id"]);
                            if (json.Value<bool?>("has_more") != true) break;
                            after = (string)json["last_id"];
                            if (after == null) break;
                        }
                    }
                    else
                    {
                        var json = JObject.Parse(Http("GET", settings.BaseUrl + "/models", BearerHeaders(settings), null, 20));
                        foreach (var m in json["data"] ?? new JArray()) ids.Add((string)m["id"]);
                        if (provider == AiProviders.OpenAI) ids = ids.Where(IsOpenAiChatModel).ToList();
                    }
                    ids = ids.Where(i => !string.IsNullOrEmpty(i)).Distinct().OrderBy(i => i, StringComparer.OrdinalIgnoreCase).ToList();
                    return (ids, (string)null);
                }
                catch (Exception e)
                {
                    return (new List<string>(), e.Message);
                }
            });
        }

        static bool IsOpenAiChatModel(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            string l = id.ToLowerInvariant();
            if (!(l.StartsWith("gpt-") || l.StartsWith("chatgpt") || (l.Length > 1 && l[0] == 'o' && char.IsDigit(l[1])))) return false;
            string[] excluded = { "audio", "realtime", "tts", "transcribe", "image", "search", "embedding", "instruct", "moderation", "codex", "computer" };
            return !excluded.Any(l.Contains);
        }

        // ---------------- OpenAI / DeepSeek (format chat/completions) ----------------

        static AiResult ChatOpenAiCompatible(string provider, ProviderSettings s, string system, List<ChatMessage> history, int maxTokens, int timeout)
        {
            var messages = new JArray { new JObject { ["role"] = "system", ["content"] = system } };
            foreach (var m in history) messages.Add(new JObject { ["role"] = m.Role, ["content"] = m.Content });

            var body = new JObject { ["model"] = s.Model, ["messages"] = messages };
            // Les modèles OpenAI récents refusent "max_tokens" ; DeepSeek utilise encore "max_tokens".
            body[provider == AiProviders.OpenAI ? "max_completion_tokens" : "max_tokens"] = maxTokens;

            var json = JObject.Parse(Http("POST", s.BaseUrl + "/chat/completions", BearerHeaders(s), body.ToString(Formatting.None), timeout));
            var result = new AiResult
            {
                InputTokens = json["usage"]?.Value<long?>("prompt_tokens") ?? 0,
                OutputTokens = json["usage"]?.Value<long?>("completion_tokens") ?? 0,
            };
            var choice = json["choices"]?.FirstOrDefault();
            string text = choice?["message"]?["content"]?.Type == JTokenType.String ? (string)choice["message"]["content"] : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                string reason = (string)choice?["finish_reason"];
                result.Error = reason == "length"
                    ? "réponse vide (limite de tokens atteinte, augmentez MaxOutputTokens)"
                    : "réponse vide du fournisseur" + (reason != null ? " (" + reason + ")" : "");
                return result;
            }
            result.Success = true;
            result.Text = text;
            return result;
        }

        // ---------------- Anthropic (Messages API) ----------------

        static AiResult ChatAnthropic(ProviderSettings s, string system, List<ChatMessage> history, int maxTokens, int timeout)
        {
            var messages = new JArray();
            foreach (var m in history) messages.Add(new JObject { ["role"] = m.Role, ["content"] = m.Content });

            var body = new JObject
            {
                ["model"] = s.Model,
                ["max_tokens"] = maxTokens,
                ["system"] = system,
                ["messages"] = messages,
            };
            bool recent = SupportsEffortAndFallback(s.Model);
            if (recent)
            {
                // Réplique courte de dialogue : effort bas. Repli automatique côté serveur en cas de refus.
                body["output_config"] = new JObject { ["effort"] = "low" };
                body["fallbacks"] = "default";
            }

            var json = JObject.Parse(Http("POST", s.BaseUrl + "/v1/messages", AnthropicHeaders(s, recent), body.ToString(Formatting.None), timeout));
            var result = new AiResult
            {
                InputTokens = json["usage"]?.Value<long?>("input_tokens") ?? 0,
                OutputTokens = json["usage"]?.Value<long?>("output_tokens") ?? 0,
            };
            string stop = (string)json["stop_reason"];
            if (stop == "refusal")
            {
                result.Error = "le modèle a refusé de répondre";
                return result;
            }
            var sb = new StringBuilder();
            foreach (var block in json["content"] ?? new JArray())
                if ((string)block["type"] == "text") sb.Append((string)block["text"]);
            if (sb.Length == 0 || string.IsNullOrWhiteSpace(sb.ToString()))
            {
                result.Error = stop == "max_tokens"
                    ? "réponse vide (limite de tokens atteinte, augmentez MaxOutputTokens)"
                    : "réponse vide du fournisseur";
                return result;
            }
            result.Success = true;
            result.Text = sb.ToString();
            return result;
        }

        /// <summary>Modèles acceptant output_config.effort et le paramètre fallbacks "default".</summary>
        static bool SupportsEffortAndFallback(string model)
        {
            if (string.IsNullOrEmpty(model)) return false;
            string[] prefixes = { "claude-opus-5", "claude-fable-5", "claude-sonnet-5-5" };
            return prefixes.Any(p => model.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        static Dictionary<string, string> AnthropicHeaders(ProviderSettings s, bool fallbackBeta)
        {
            var h = new Dictionary<string, string>
            {
                ["x-api-key"] = s.ApiKey,
                ["anthropic-version"] = AnthropicVersion,
            };
            if (fallbackBeta) h["anthropic-beta"] = AnthropicFallbackBeta;
            return h;
        }

        static Dictionary<string, string> BearerHeaders(ProviderSettings s) =>
            new Dictionary<string, string> { ["Authorization"] = "Bearer " + s.ApiKey };

        // ---------------- HTTP ----------------

        static string Http(string method, string url, Dictionary<string, string> headers, string body, int timeoutSeconds)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = method;
            req.Timeout = timeoutSeconds * 1000;
            req.ReadWriteTimeout = timeoutSeconds * 1000;
            req.Accept = "application/json";
            req.UserAgent = "PNJCreator/1.0";
            foreach (var kv in headers) req.Headers[kv.Key] = kv.Value;

            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                req.ContentType = "application/json";
                req.ContentLength = bytes.Length;
                using (var stream = req.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException e) when (e.Response is HttpWebResponse errResp)
            {
                string errBody = "";
                try
                {
                    using (var reader = new StreamReader(errResp.GetResponseStream(), Encoding.UTF8)) errBody = reader.ReadToEnd();
                }
                catch { }
                throw new Exception($"HTTP {(int)errResp.StatusCode} : {ExtractErrorMessage(errBody)}");
            }
        }

        static string ExtractErrorMessage(string body)
        {
            try
            {
                var j = JObject.Parse(body);
                string msg = (string)j["error"]?["message"] ?? (string)j["message"];
                if (!string.IsNullOrEmpty(msg)) return msg;
            }
            catch { }
            return body.Length > 200 ? body.Substring(0, 200) : body;
        }
    }
}
