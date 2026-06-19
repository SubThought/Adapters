/**********************************************************************************************
  Copyright(c) 2013-2026 SubThought Corporation. All Rights Reserved.

  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS
  OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.

  IN NO EVENT SHALL THE AUTHOR(S) OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
  DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
  ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE, ITS USE, OR OTHER
  DEALINGS IN THE SOFTWARE.

 **********************************************************************************************/

//
// NaoIO.cs — NAOqi IData adapter implementation
//
// Bridges Premise tell/ask to NAO robots via the NAOqi HTTP API.
// Compiles into nao-io.dll.  Registered via nao-io.package provider declaration.
//
// Usage in Premise:
//   (open "naoqi://nao-03:9559")
//   (ask  Nao-1 {module ALMotion :method getAngles :params {"HeadYaw" true}})
//   (tell Nao-1 {module ALMotion :method moveToward :params {0.5 0.0 0.0}})
//   (tell Nao-1 {module ALTextToSpeech :method say :params {"hello world"}})
//   (ask  Nao-1 {module ALMemory :method getData :params {"Device/SubDeviceList/HeadPitch/Position/Sensor/Value"}})
//   (close Nao-1)
//
// The NAOqi HTTP API exposes all NAOqi modules (ALMotion, ALTextToSpeech,
// ALVideoDevice, ALAudioDevice, ALMemory, ALLeds, ALBehaviorManager, etc.)
// through a uniform JSON-RPC interface on port 9559.
//
// NAOqi modules used:
//   ALMotion            — joint control, walking, stiffness, cartesian
//   ALTextToSpeech      — speech synthesis
//   ALAudioDevice       — microphone capture
//   ALVideoDevice       — camera subscriptions
//   ALSoundLocalization — sound source direction
//   ALFaceDetection     — face detection
//   ALMemory            — shared memory, events, sensor data
//   ALLeds              — LED control
//   ALBehaviorManager   — named behaviors
//   ALRobotPosture      — predefined postures (Stand, Sit, Crouch)
//   ALTabletService     — tablet display (NAO v6)
//   ALSonar             — ultrasonic sonar
//   ALBattery           — battery state
//

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Theory
{
    public partial class Premise
    {
        public sealed class NaoIO : IData, IDisposable
        {
            // ── Properties ───────────────────────────────────────────

            public PrLiteral Name { get; } = new PrLiteral("nao-io");
            public PrUrl? Url { get; private set; }
            public PrVariant Connected => _client is not null
                && _baseUrl is not null
                    ? YES : NO;

            // ── Internal state ───────────────────────────────────────

            private HttpClient? _client;
            private string? _baseUrl;
            private string? _sessionId;
            private readonly Lock _lock = new();
            private int _idCounter;


            // ── IData.TryOpen ────────────────────────────────────────
            //
            // url: naoqi://hostname:port  (default port 9559)
            // options: {:Timeout 5000 :Username "nao" :Password "nao"}

            public bool TryOpen(PrUrl url, PrIdiom? options,
                                out PrVariant result)
            {
                result = NIL;
                try
                {
                    lock (_lock)
                    {
                        _client?.Dispose();

                        var urlStr = url.Name;
                        if (urlStr.StartsWith("naoqi://",
                                StringComparison.OrdinalIgnoreCase))
                            urlStr = urlStr["naoqi://".Length..];

                        // Default port
                        if (!urlStr.Contains(':'))
                            urlStr += ":9559";

                        _baseUrl = $"http://{urlStr}";

                        var handler = new HttpClientHandler();
                        _client = new HttpClient(handler)
                        {
                            Timeout = TimeSpan.FromMilliseconds(5000)
                        };

                        // Apply options
                        if (options is not null)
                        {
                            for (int i = 0; i < options.Count - 1; i += 2)
                            {
                                if (options.Elements[i] is not PrSlot slot)
                                    continue;
                                var key = slot.Stem.ToLowerInvariant();
                                var val = options.Elements[i + 1];

                                switch (key)
                                {
                                    case "timeout":
                                        if (val is PrInteger t)
                                            _client.Timeout =
                                                TimeSpan.FromMilliseconds(
                                                    t.Value);
                                        break;
                                }
                            }
                        }

                        // Create a session with NAOqi
                        var sessionReq = new JsonObject
                        {
                            ["method"] = "ALSession.create",
                            ["params"] = new JsonArray(),
                            ["id"] = NextId()
                        };

                        var resp = CallApi(sessionReq);
                        if (resp is not null && resp["result"] is not null)
                            _sessionId = resp["result"]?.ToString();

                        // Verify connectivity by pinging ALSystem
                        var ping = new JsonObject
                        {
                            ["method"] = "ALSystem.robotName",
                            ["params"] = new JsonArray(),
                            ["id"] = NextId()
                        };

                        var pingResp = CallApi(ping);
                        if (pingResp is null)
                        {
                            _client.Dispose();
                            _client = null;
                            result = new PrString("NAOqi: no response from robot.");
                            return false;
                        }

                        Url = url;
                        result = url;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    result = new PrString(ex.Message);
                    return false;
                }
            }


            // ── IData.TryClose ───────────────────────────────────────

            public bool TryClose(out PrVariant result)
            {
                result = NIL;
                try
                {
                    lock (_lock)
                    {
                        _client?.Dispose();
                        _client = null;
                        _baseUrl = null;
                        _sessionId = null;

                        result = new PrLiteral("closed");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    result = new PrString(ex.Message);
                    return false;
                }
            }


            // ── IData.TryTell ────────────────────────────────────────
            //
            // Fire-and-forget calls to NAOqi modules.
            //
            // Premise idiom:
            //   {module ALMotion :method moveToward :params {0.5 0.0 0.0}}
            //   {module ALTextToSpeech :method say :params {"hello"}}
            //   {module ALLeds :method fadeRGB :params {"FaceLeds" 0x0000FF 1.0}}
            //   {module ALRobotPosture :method goToPosture :params {"Stand" 0.8}}

            public bool TryTell(PrString command, PrIdiom? parameters,
                                out PrVariant result)
            {
                result = NIL;
                try
                {
                    lock (_lock)
                    {
                        if (_client is null)
                        {
                            result = new PrString("Connection is not open.");
                            return false;
                        }

                        var idiom = ParseIdiom(parameters);
                        var module = GetString(idiom, "module");
                        var method = GetString(idiom, "method");

                        if (module is null || method is null)
                        {
                            result = new PrString(
                                "Use {module ALModule :method methodName :params {...}}.");
                            return false;
                        }

                        var rpc = new JsonObject
                        {
                            ["method"] = $"{module}.{method}",
                            ["params"] = GetParams(idiom),
                            ["id"] = NextId()
                        };

                        var resp = CallApi(rpc);
                        result = new PrLiteral("called");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    result = new PrString(ex.Message);
                    return false;
                }
            }


            // ── IData.TryAsk ─────────────────────────────────────────
            //
            // Calls a NAOqi module method and returns the result.
            //
            // Premise idiom:
            //   {module ALMotion :method getAngles :params {"HeadYaw" true}}
            //   {module ALMemory :method getData :params {"BatteryChargeChanged"}}
            //   {module ALVideoDevice :method getImageRemote :params {"camera_sub_id"}}
            //   {module ALBattery :method getBatteryCharge :params {}}
            //   {module ALRobotPosture :method getPostureFamily :params {}}

            public bool TryAsk(PrString query, PrIdiom? parameters,
                               NativeInteger skip, NativeInteger limit,
                               out PrVariant result)
            {
                result = NIL;
                try
                {
                    lock (_lock)
                    {
                        if (_client is null)
                        {
                            result = new PrString("Connection is not open.");
                            return false;
                        }

                        var idiom = ParseIdiom(parameters);
                        var module = GetString(idiom, "module");
                        var method = GetString(idiom, "method");

                        if (module is null || method is null)
                        {
                            result = new PrString(
                                "Use {module ALModule :method methodName :params {...}}.");
                            return false;
                        }

                        var rpc = new JsonObject
                        {
                            ["method"] = $"{module}.{method}",
                            ["params"] = GetParams(idiom),
                            ["id"] = NextId()
                        };

                        var resp = CallApi(rpc);

                        if (resp is null)
                        {
                            result = new PrString("No response from NAOqi.");
                            return false;
                        }

                        if (resp["error"] is not null
                            && resp["error"]!.ToString() != "null")
                        {
                            result = new PrString(
                                resp["error"]!.ToString());
                            return false;
                        }

                        var value = resp["result"];
                        result = value is not null
                            ? JsonToPremise(value) : NIL;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    result = new PrString(ex.Message);
                    return false;
                }
            }


            // ── HTTP API call ────────────────────────────────────────

            private JsonNode? CallApi(JsonObject rpc)
            {
                if (_client is null || _baseUrl is null) return null;

                var json = rpc.ToJsonString();
                var content = new StringContent(
                    json, Encoding.UTF8, "application/json");

                var response = _client.PostAsync(
                    $"{_baseUrl}/api", content).Result;

                if (!response.IsSuccessStatusCode)
                    return null;

                var body = response.Content
                    .ReadAsStringAsync().Result;

                return JsonNode.Parse(body);
            }


            // ── Idiom parsing ────────────────────────────────────────

            private static JsonObject ParseIdiom(PrIdiom? parameters)
            {
                var result = new JsonObject();
                if (parameters is null) return result;

                for (int i = 0; i < parameters.Count - 1; i += 2)
                {
                    string key;
                    if (parameters.Elements[i] is PrSlot slot)
                        key = slot.Stem;
                    else if (parameters.Elements[i] is PrLiteral lit)
                        key = lit.Name;
                    else
                        continue;

                    result[key] = PremiseToJson(
                        parameters.Elements[i + 1]);
                }
                return result;
            }

            private static string? GetString(JsonObject obj, string key)
            {
                return obj.ContainsKey(key) ? obj[key]?.ToString() : null;
            }

            private static JsonArray GetParams(JsonObject idiom)
            {
                if (idiom.ContainsKey("params")
                    && idiom["params"] is JsonArray arr)
                    return arr;

                if (idiom.ContainsKey("params")
                    && idiom["params"] is JsonNode node)
                {
                    // Single param wrapped
                    var a = new JsonArray();
                    a.Add(node.DeepClone());
                    return a;
                }

                return new JsonArray();
            }


            // ── JSON ↔ Premise conversion ────────────────────────────

            private static PrVariant JsonToPremise(JsonNode node)
            {
                switch (node)
                {
                    case JsonObject obj:
                    {
                        var elements = new List<PrVariant>();
                        foreach (var kvp in obj)
                        {
                            elements.Add(new PrSlot(kvp.Key));
                            elements.Add(kvp.Value is not null
                                ? JsonToPremise(kvp.Value) : NIL);
                        }
                        return Misc.Idiom(elements);
                    }

                    case JsonArray arr:
                    {
                        var list = Misc.List();
                        foreach (var item in arr)
                        {
                            list.Elements.Add(item is not null
                                ? JsonToPremise(item) : NIL);
                        }
                        return list;
                    }

                    case JsonValue val:
                    {
                        if (val.TryGetValue<long>(out var lng))
                            return new PrInteger(lng);
                        if (val.TryGetValue<double>(out var dbl))
                            return new PrDecimal((NativeDecimal)dbl);
                        if (val.TryGetValue<bool>(out var bln))
                            return bln ? YES : NO;
                        if (val.TryGetValue<string>(out var str))
                            return new PrString(str);
                        return NIL;
                    }

                    default:
                        return NIL;
                }
            }

            private static JsonNode? PremiseToJson(PrVariant val)
            {
                return val.Tag switch
                {
                    var t when t == The.Integer
                        => JsonValue.Create(
                            Taxons.Integer.Cast(val).Value),
                    var t when t == The.Decimal
                        => JsonValue.Create(
                            (double)Taxons.Decimal.Cast(val).Value),
                    var t when t == The.String
                        => JsonValue.Create(
                            Taxons.String.Cast(val).Text),
                    var t when t == The.Literal
                        => JsonValue.Create(val.ToText()),
                    var t when t == The.Nil
                        => null,
                    var t when t == The.Idiom
                        => IdiomToJsonObject((PrIdiom)val),
                    var t when t == The.List
                        => ListToJsonArray((PrList)val),
                    _ => JsonValue.Create(val.ToText())
                };
            }

            private static JsonObject IdiomToJsonObject(PrIdiom idiom)
            {
                var obj = new JsonObject();
                for (int i = 0; i < idiom.Count - 1; i += 2)
                {
                    var key = idiom.Elements[i] is PrSlot s
                        ? s.Stem : idiom.Elements[i].ToText();
                    obj[key] = PremiseToJson(idiom.Elements[i + 1]);
                }
                return obj;
            }

            private static JsonArray ListToJsonArray(PrList list)
            {
                var arr = new JsonArray();
                foreach (var el in list.Elements)
                    arr.Add(PremiseToJson(el));
                return arr;
            }

            private string NextId()
            {
                return $"premise-{Interlocked.Increment(ref _idCounter)}";
            }


            // ── IDisposable ──────────────────────────────────────────

            public void Dispose()
            {
                lock (_lock)
                {
                    _client?.Dispose();
                    _client = null;
                    _baseUrl = null;
                    _sessionId = null;
                }
            }
        }

    } // end partial class Premise

} // end namespace Theory
