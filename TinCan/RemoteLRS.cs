﻿/*
    Copyright 2014 Rustici Software
    Modifications copyright (C) 2018 Neal Daniel

    Licensed under the Apache License, Version 2.0 (the "License");
    you may not use this file except in compliance with the License.
    You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

    Unless required by applicable law or agreed to in writing, software
    distributed under the License is distributed on an "AS IS" BASIS,
    WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
    See the License for the specific language governing permissions and
    limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TinCan.Documents;
using TinCan.Json;
using TinCan.LrsResponses;
using UnityEngine;
using UnityEngine.Networking;

namespace TinCan
{
    public class RemoteLrs : ILrs
    {
        public Uri Endpoint { get; set; }
        public TCAPIVersion Version { get; set; }
        public string Auth { get; set; }
        public Dictionary<string, string> Extended { get; set; }

        public void SetAuth(string username, string password)
        {
            Auth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password));
        }

        public RemoteLrs() { }

        public RemoteLrs(Uri endpoint, TCAPIVersion version, string username, string password)
        {
            Endpoint = endpoint;
            Version = version;
            SetAuth(username, password);
        }

        public RemoteLrs(string endpoint, TCAPIVersion version, string username, string password) : this(new Uri(endpoint), version, username, password) { }
        public RemoteLrs(string endpoint, string username, string password) : this(endpoint, TCAPIVersion.Latest(), username, password) { }

        private class MyHttpRequest
        {
            public string Method { get; set; }
            public string Resource { get; set; }
            public Dictionary<string, string> QueryParams { get; set; }
            public Dictionary<string, string> Headers { get; set; }
            public string ContentType { get; set; }
            public byte[] Content { get; set; }
        }

        private class MyHttpResponse
        {
            public HttpStatusCode Status { get; }
            public string ContentType { get; }
            public byte[] Content { get; set; }
            public DateTime LastModified { get; }
            public string Etag { get; }
            public Exception Ex { get; set; }

            public MyHttpResponse() { }

            public MyHttpResponse(UnityWebRequest request, UnityWebRequest.Result result)
            {
                HttpStatusCode statusCode;
                switch (result)
                {
                    case UnityWebRequest.Result.Success:
                        statusCode = HttpStatusCode.OK;
                        break;
                    default:
                        statusCode = HttpStatusCode.NotImplemented;
                        break;
                }

                Status = statusCode;

                Dictionary<string, string> responseHeaders = request.GetResponseHeaders();
                Debug.Log($"{responseHeaders.Count} responseHeaders");
                foreach (KeyValuePair<string, string> pair in responseHeaders)
                {
                    Debug.Log($"  {pair.Key}:{pair.Value}");
                }

                if (responseHeaders.ContainsKey("Etag"))
                {
                    Etag = responseHeaders["Etag"];
                }

                try
                {
                    if (responseHeaders.ContainsKey("Last-Modified"))
                    {
                        LastModified = DateTime.Parse(responseHeaders["Last-Modified"]);
                    }
                    else
                    {
                        LastModified = DateTime.Now;
                    }
                }
                catch
                {
                    //sometimes will throw an exception, just ignore
                }

                DownloadHandler downloadHandler = request.downloadHandler;
                Content = downloadHandler.data;
            }

            public MyHttpResponse(HttpWebResponse webResp)
            {
                Status = webResp.StatusCode;
                ContentType = webResp.ContentType;
                Etag = webResp.Headers.Get("Etag");

                try
                {
                    LastModified = webResp.LastModified;
                }
                catch
                {
                    //sometimes will throw an exception, just ignore
                }

                using (Stream stream = webResp.GetResponseStream())
                {
                    Content = ReadFully(stream, (int) webResp.ContentLength);
                }
            }
        }

        private Task<MyHttpResponse> MakeRequest(MyHttpRequest req)
        {
            //return MakeHttpRequest(req);
            return MakeUnityRequest(req);
        }

        private async Task<MyHttpResponse> MakeHttpRequest(MyHttpRequest req)
        {
            string url;
            if (req.Resource.StartsWith("http", StringComparison.InvariantCultureIgnoreCase))
            {
                url = req.Resource;
            }
            else
            {
                url = Endpoint.ToString();
                if (!url.EndsWith("/") && !req.Resource.StartsWith("/"))
                {
                    url += "/";
                }

                url += req.Resource;
            }

            if (req.QueryParams != null)
            {
                string qs = "";
                foreach (KeyValuePair<string, string> entry in req.QueryParams)
                {
                    if (qs != "")
                    {
                        qs += "&";
                    }

                    qs += Uri.EscapeUriString(entry.Key) + "=" + Uri.EscapeUriString(entry.Value);
                }

                if (qs != "")
                {
                    url += "?" + qs;
                }
            }

            // TODO: handle special properties we recognize, such as content type, modified since, etc.
            HttpWebRequest webReq = (HttpWebRequest) WebRequest.Create(url);
            webReq.Method = req.Method;

            webReq.Headers.Add("X-Experience-API-Version", Version.ToString());
            if (Auth != null)
            {
                webReq.Headers.Add("Authorization", Auth);
            }

            if (req.Headers != null)
            {
                foreach (KeyValuePair<string, string> entry in req.Headers)
                {
                    webReq.Headers.Add(entry.Key, entry.Value);
                }
            }

            webReq.ContentType = req.ContentType ?? "application/octet-stream";

            if (req.Content != null)
            {
                webReq.ContentLength = req.Content.Length;
                using (Stream stream = webReq.GetRequestStream())
                {
                    stream.Write(req.Content, 0, req.Content.Length);
                }
            }

            MyHttpResponse resp;

            try
            {
                using (WebResponse webResp = await webReq.GetResponseAsync())
                {
                    HttpWebResponse test = webResp as HttpWebResponse;
                    resp = new MyHttpResponse(test);
                }
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                {
                    using (HttpWebResponse webResp = (HttpWebResponse) ex.Response)
                    {
                        resp = new MyHttpResponse(webResp);
                    }
                }
                else
                {
                    resp = new MyHttpResponse
                    {
                        Content = Encoding.UTF8.GetBytes("Web exception without '.Response'")
                    };
                }

                resp.Ex = ex;
            }

            return resp;
        }

        private async Task<MyHttpResponse> MakeUnityRequest(MyHttpRequest req)
        {
            string url;
            if (req.Resource.StartsWith("http", StringComparison.InvariantCultureIgnoreCase))
            {
                url = req.Resource;
            }
            else
            {
                url = Endpoint.ToString();
                if (!url.EndsWith("/") && !req.Resource.StartsWith("/"))
                {
                    url += "/";
                }

                url += req.Resource;
            }

            if (req.QueryParams != null)
            {
                string qs = "";
                foreach (KeyValuePair<string, string> entry in req.QueryParams)
                {
                    if (qs != "")
                    {
                        qs += "&";
                    }

                    qs += Uri.EscapeUriString(entry.Key) + "=" + Uri.EscapeUriString(entry.Value);
                }

                if (qs != "")
                {
                    url += "?" + qs;
                }
            }

            Debug.Log($"WebRequest Create {req.Method} {url}");
            // TODO: handle special properties we recognize, such as content type, modified since, etc.
            UnityWebRequest request = new UnityWebRequest(url);
            request.method = req.Method;
            request.downloadHandler = (DownloadHandler) new DownloadHandlerBuffer();

            request.SetRequestHeader("X-Experience-API-Version", Version.ToString());
            if (Auth != null)
            {
                request.SetRequestHeader("Authorization", Auth);
            }

            if (req.Headers != null)
            {
                foreach (KeyValuePair<string, string> entry in req.Headers)
                {
                    req.Headers.Add(entry.Key, entry.Value);
                }
            }

            req.ContentType ??= "application/octet-stream";
            request.SetRequestHeader("Content-Type", req.ContentType);

            Debug.Log($"ContentType:{req.ContentType}");

            if (req.Content != null)
            {
                request.uploadHandler = (UploadHandler) new UploadHandlerRaw(req.Content);
                //request.uploadHandler.contentType = "application/x-www-form-urlencoded";
                // request.ContentLength = req.content.Length;
                // using (var stream = request.GetRequestStream())
                // {
                //     stream.Write(req.content, 0, req.content.Length);
                // }
            }

            MyHttpResponse resp = null;

            try
            {
                UnityWebRequest.Result result = await request.SendWebRequest();
                resp = new MyHttpResponse(request, result);
                // using (var webResp = (HttpWebResponse)request.GetResponse())
                // {
                //     resp = new MyHTTPResponse(webResp);
                // }
            }
            catch (WebException ex)
            {
                if (ex.Response != null)
                {
                    using (HttpWebResponse webResp = (HttpWebResponse) ex.Response)
                    {
                        resp = new MyHttpResponse(webResp);
                    }
                }
                else
                {
                    resp = new MyHttpResponse
                    {
                        Content = Encoding.UTF8.GetBytes("Web exception without '.Response'")
                    };
                }

                resp.Ex = ex;
            }

            Debug.Log($"HttpWebResponse {resp}");

            return resp;
        }

        /// <summary>
        /// See http://www.yoda.arachsys.com/csharp/readbinary.html no license found
        /// 
        /// Reads data from a stream until the end is reached. The
        /// data is returned as a byte array. An IOException is
        /// thrown if any of the underlying IO calls fail.
        /// </summary>
        /// <param name="stream">The stream to read data from</param>
        /// <param name="initialLength">The initial buffer length</param>
        private static byte[] ReadFully(Stream stream, int initialLength)
        {
            // If we've been passed an unhelpful initial length, just
            // use 32K.
            if (initialLength < 1)
            {
                initialLength = 32768;
            }

            byte[] buffer = new byte[initialLength];
            int read = 0;

            int chunk;
            while ((chunk = stream.Read(buffer, read, buffer.Length - read)) > 0)
            {
                read += chunk;

                // If we've reached the end of our buffer, check to see if there's
                // any more information
                if (read == buffer.Length)
                {
                    int nextByte = stream.ReadByte();

                    // End of stream? If so, we're done
                    if (nextByte == -1)
                    {
                        return buffer;
                    }

                    // Nope. Resize the buffer, put in the byte we've just
                    // read, and continue
                    byte[] newBuffer = new byte[buffer.Length * 2];
                    Array.Copy(buffer, newBuffer, buffer.Length);
                    newBuffer[read] = (byte) nextByte;
                    buffer = newBuffer;
                    read++;
                }
            }

            // Buffer is now too big. Shrink it.
            byte[] ret = new byte[read];
            Array.Copy(buffer, ret, read);
            return ret;
        }

        private async Task<MyHttpResponse> GetDocument(string resource, Dictionary<string, string> queryParams, Document document)
        {
            MyHttpRequest req = new MyHttpRequest
            {
                Method = "GET",
                Resource = resource,
                QueryParams = queryParams,
                ContentType = "application/json"
            };

            MyHttpResponse res = await MakeRequest(req);
            if (res.Status == HttpStatusCode.OK)
            {
                document.Content = res.Content;
                document.ContentType = res.ContentType;
                document.Timestamp = res.LastModified;
                document.Etag = res.Etag;
            }

            return res;
        }

        private async Task<ProfileKeysLrsResponse> GetProfileKeys(string resource, Dictionary<string, string> queryParams)
        {
            ProfileKeysLrsResponse r = new ProfileKeysLrsResponse();

            MyHttpRequest req = new MyHttpRequest
            {
                Method = "GET",
                Resource = resource,
                QueryParams = queryParams
            };

            MyHttpResponse res = await MakeRequest(req);
            if (res.Status != HttpStatusCode.OK)
            {
                r.Success = false;
                r.HttpException = res.Ex;
                r.SetErrMsgFromBytes(res.Content);
                return r;
            }

            r.Success = true;

            JArray keys = JArray.Parse(Encoding.UTF8.GetString(res.Content));
            if (keys.Count > 0)
            {
                r.Content = new List<string>();
                foreach (JToken key in keys)
                {
                    r.Content.Add((string) key);
                }
            }

            return r;
        }

        private async Task<LrsResponse> SaveDocument(string resource, Dictionary<string, string> queryParams, Document document)
        {
            LrsResponse r = new LrsResponse();

            MyHttpRequest req = new MyHttpRequest
            {
                Method = "PUT",
                Resource = resource,
                QueryParams = queryParams,
                ContentType = document.ContentType,
                Content = document.Content
            };
            if (!string.IsNullOrEmpty(document.Etag))
            {
                req.Headers = new Dictionary<string, string> { { "If-Match", document.Etag } };
            }
            else
            {
                req.Headers = new Dictionary<string, string> { { "If-None-Match", "*" } };
            }

            MyHttpResponse res = await MakeRequest(req);
            if (res.Status != HttpStatusCode.NoContent)
            {
                r.Success = false;
                r.HttpException = res.Ex;
                r.SetErrMsgFromBytes(res.Content);
                return r;
            }

            r.Success = true;

            return r;
        }

        private async Task<LrsResponse> DeleteDocument(string resource, Dictionary<string, string> queryParams)
        {
            LrsResponse r = new LrsResponse();

            MyHttpRequest req = new MyHttpRequest
            {
                Method = "DELETE",
                Resource = resource,
                QueryParams = queryParams
            };

            MyHttpResponse res = await MakeRequest(req);
            if (res.Status != HttpStatusCode.NoContent)
            {
                r.Success = false;
                r.HttpException = res.Ex;
                r.SetErrMsgFromBytes(res.Content);
                return r;
            }

            r.Success = true;

            return r;
        }

        private async Task<StatementLrsResponse> GetStatement(Dictionary<string, string> queryParams)
        {
            StatementLrsResponse r = new StatementLrsResponse();

            MyHttpRequest req = new MyHttpRequest
            {
                Method = "GET",
                Resource = "statements",
                QueryParams = queryParams
            };

            MyHttpResponse res = await MakeRequest(req);
            if (res.Status != HttpStatusCode.OK)
            {
                r.Success = false;
                r.HttpException = res.Ex;
                r.SetErrMsgFromBytes(res.Content);
                return r;
            }

            r.Success = true;
            r.Content = new Statement(new Json.StringOfJson(Encoding.UTF8.GetString(res.Content)));

            return r;
        }

        public async Task<AboutLrsResponse> AboutAsync()
        {
            AboutLrsResponse r = new AboutLrsResponse();

            MyHttpRequest req = new MyHttpRequest
            {
                Method = "GET",
                Resource = "about"
            };

            MyHttpResponse res = await MakeRequest(req);
            if (res.Status != HttpStatusCode.OK)
            {
                r.Success = false;
                r.HttpException = res.Ex;
                r.SetErrMsgFromBytes(res.Content);
                return r;
            }

            r.Success = true;
            r.Content = new About(Encoding.UTF8.GetString(res.Content));

            return r;
        }

        public async Task<StatementLrsResponse> SaveStatementAsync(Statement statement)
        {
            StatementLrsResponse r = new StatementLrsResponse();
            MyHttpRequest req = new MyHttpRequest
            {
                QueryParams = new Dictionary<string, string>(),
                Resource = "statements"
            };

            if (statement.Id == null)
            {
                req.Method = "POST";
            }
            else
            {
                req.Method = "PUT";
                req.QueryParams.Add("statementId", statement.Id.ToString());
            }

            req.ContentType = "application/json";
            req.Content = Encoding.UTF8.GetBytes(statement.ToJson(Version));

            MyHttpResponse res = await MakeRequest(req);
            if (statement.Id == null)
            {
                if (res.Status != HttpStatusCode.OK)
                {
                    r.Success = false;
                    r.HttpException = res.Ex;
                    r.SetErrMsgFromBytes(res.Content);
                    return r;
                }

                JArray ids = JArray.Parse(Encoding.UTF8.GetString(res.Content));
                statement.Id = new Guid((string) ids[0]);
            }
            else
            {
                if (res.Status != HttpStatusCode.NoContent)
                {
                    r.Success = false;
                    r.HttpException = res.Ex;
                    r.SetErrMsgFromBytes(res.Content);
                    return r;
                }
            }

            r.Success = true;
            r.Content = statement;

            return r;
        }

        public async Task<StatementLrsResponse> VoidStatementAsync(Guid id, Agent agent)
        {
            Statement voidStatement = new Statement
            {
                Actor = agent,
                Verb = new Verb
                {
                    Id = new Uri("http://adlnet.gov/expapi/verbs/voided"),
                    Display = new LanguageMap()
                },
                Target = new StatementRef { Id = id }
            };
            voidStatement.Verb.Display.Add("en-US", "voided");

            return await SaveStatementAsync(voidStatement);
        }

        public async Task<StatementsResultLrsResponse> SaveStatementsAsync(List<Statement> statements)
        {
            StatementsResultLrsResponse r = new StatementsResultLrsResponse();

            MyHttpRequest req = new MyHttpRequest
            {
                Resource = "statements",
                Method = "POST",
                ContentType = "application/json"
            };

            JArray jarray = new JArray();
            foreach (Statement st in statements)
            {
                jarray.Add(st.ToJObject(Version));
            }

            req.Content = Encoding.UTF8.GetBytes(jarray.ToString());

            MyHttpResponse res = await MakeRequest(req);
            if (res.Status != HttpStatusCode.OK)
            {
                r.Success = false;
                r.HttpException = res.Ex;
                r.SetErrMsgFromBytes(res.Content);
                return r;
            }

            JArray ids = JArray.Parse(Encoding.UTF8.GetString(res.Content));
            for (int i = 0; i < ids.Count; i++)
            {
                statements[i].Id = new Guid((string) ids[i]);
            }

            r.Success = true;
            r.Content = new StatementsResult(statements);

            return r;
        }

        public async Task<StatementLrsResponse> RetrieveStatementAsync(Guid id)
        {
            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "statementId", id.ToString() }
            };

            return await GetStatement(queryParams);
        }

        public async Task<StatementLrsResponse> RetrieveVoidedStatementAsync(Guid id)
        {
            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "voidedStatementId", id.ToString() }
            };

            return await GetStatement(queryParams);
        }

        public async Task<StatementsResultLrsResponse> QueryStatementsAsync(StatementsQuery query)
        {
            StatementsResultLrsResponse r = new StatementsResultLrsResponse();

            MyHttpRequest req = new MyHttpRequest
            {
                Method = "GET",
                Resource = "statements",
                QueryParams = query.ToParameterMap(Version)
            };

            MyHttpResponse res = await MakeRequest(req);
            if (res.Status != HttpStatusCode.OK)
            {
                r.Success = false;
                r.HttpException = res.Ex;
                r.SetErrMsgFromBytes(res.Content);
                return r;
            }

            r.Success = true;
            r.Content = new StatementsResult(new Json.StringOfJson(Encoding.UTF8.GetString(res.Content)));

            return r;
        }

        public async Task<StatementsResultLrsResponse> MoreStatementsAsync(StatementsResult result)
        {
            StatementsResultLrsResponse r = new StatementsResultLrsResponse();

            MyHttpRequest req = new MyHttpRequest
            {
                Method = "GET",
                Resource = Endpoint.GetLeftPart(UriPartial.Authority)
            };
            if (!req.Resource.EndsWith("/"))
            {
                req.Resource += "/";
            }

            req.Resource += result.More;

            MyHttpResponse res = await MakeRequest(req);
            if (res.Status != HttpStatusCode.OK)
            {
                r.Success = false;
                r.HttpException = res.Ex;
                r.SetErrMsgFromBytes(res.Content);
                return r;
            }

            r.Success = true;
            StringOfJson json = new Json.StringOfJson(Encoding.UTF8.GetString(res.Content));
            r.Content = new StatementsResult(json);

            return r;
        }

        // TODO: since param
        public async Task<ProfileKeysLrsResponse> RetrieveStateIdsAsync(Activity activity, Agent agent,
            Guid? registration = null)
        {
            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "activityId", activity.Id },
                { "agent", agent.ToJson(Version) }
            };
            if (registration != null)
            {
                queryParams.Add("registration", registration.ToString());
            }

            return await GetProfileKeys("activities/state", queryParams);
        }

        public async Task<StateLrsResponse> RetrieveStateAsync(string id, Activity activity, Agent agent,
            Guid? registration = null)
        {
            StateLrsResponse r = new StateLrsResponse();

            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "stateId", id },
                { "activityId", activity.Id },
                { "agent", agent.ToJson(Version) }
            };

            StateDocument state = new StateDocument
            {
                Id = id,
                Activity = activity,
                Agent = agent
            };

            if (registration != null)
            {
                queryParams.Add("registration", registration.ToString());
                state.Registration = registration;
            }

            MyHttpResponse resp = await GetDocument("activities/state", queryParams, state);
            if (resp.Status != HttpStatusCode.OK && resp.Status != HttpStatusCode.NotFound)
            {
                r.Success = false;
                r.HttpException = resp.Ex;
                r.SetErrMsgFromBytes(resp.Content);
                return r;
            }

            r.Success = true;
            r.Content = state;

            return r;
        }

        public async Task<LrsResponse> SaveStateAsync(StateDocument state)
        {
            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "stateId", state.Id },
                { "activityId", state.Activity.Id },
                { "agent", state.Agent.ToJson(Version) }
            };
            if (state.Registration != null)
            {
                queryParams.Add("registration", state.Registration.ToString());
            }

            return await SaveDocument("activities/state", queryParams, state);
        }

        public async Task<LrsResponse> DeleteStateAsync(StateDocument state)
        {
            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "stateId", state.Id },
                { "activityId", state.Activity.Id },
                { "agent", state.Agent.ToJson(Version) }
            };
            if (state.Registration != null)
            {
                queryParams.Add("registration", state.Registration.ToString());
            }

            return await DeleteDocument("activities/state", queryParams);
        }

        public async Task<LrsResponse> ClearStateAsync(Activity activity, Agent agent, Guid? registration = null)
        {
            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "activityId", activity.Id },
                { "agent", agent.ToJson(Version) }
            };
            if (registration != null)
            {
                queryParams.Add("registration", registration.ToString());
            }

            return await DeleteDocument("activities/state", queryParams);
        }

        // TODO: since param
        public async Task<ProfileKeysLrsResponse> RetrieveActivityProfileIdsAsync(Activity activity)
        {
            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "activityId", activity.Id }
            };

            return await GetProfileKeys("activities/profile", queryParams);
        }

        public async Task<ActivityProfileLrsResponse> RetrieveActivityProfileAsync(string id, Activity activity)
        {
            ActivityProfileLrsResponse r = new ActivityProfileLrsResponse();

            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "profileId", id },
                { "activityId", activity.Id }
            };

            ActivityProfileDocument profile = new ActivityProfileDocument
            {
                Id = id,
                Activity = activity
            };

            MyHttpResponse resp = await GetDocument("activities/profile", queryParams, profile);
            if (resp.Status != HttpStatusCode.OK && resp.Status != HttpStatusCode.NotFound)
            {
                r.Success = false;
                r.HttpException = resp.Ex;
                r.SetErrMsgFromBytes(resp.Content);
                return r;
            }

            r.Success = true;
            r.Content = profile;

            return r;
        }

        public async Task<LrsResponse> SaveActivityProfileAsync(ActivityProfileDocument profile)
        {
            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "profileId", profile.Id },
                { "activityId", profile.Activity.Id }
            };

            return await SaveDocument("activities/profile", queryParams, profile);
        }

        public async Task<LrsResponse> DeleteActivityProfileAsync(ActivityProfileDocument profile)
        {
            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "profileId", profile.Id },
                { "activityId", profile.Activity.Id }
            };
            // TODO: need to pass Etag?

            return await DeleteDocument("activities/profile", queryParams);
        }

        // TODO: since param
        public async Task<ProfileKeysLrsResponse> RetrieveAgentProfileIdsAsync(Agent agent)
        {
            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "agent", agent.ToJson(Version) }
            };

            return await GetProfileKeys("agents/profile", queryParams);
        }

        public async Task<AgentProfileLrsResponse> RetrieveAgentProfileAsync(string id, Agent agent)
        {
            AgentProfileLrsResponse r = new AgentProfileLrsResponse();

            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "profileId", id },
                { "agent", agent.ToJson(Version) }
            };

            AgentProfileDocument profile = new AgentProfileDocument
            {
                Id = id,
                Agent = agent
            };

            MyHttpResponse resp = await GetDocument("agents/profile", queryParams, profile);
            if (resp.Status != HttpStatusCode.OK && resp.Status != HttpStatusCode.NotFound)
            {
                r.Success = false;
                r.HttpException = resp.Ex;
                r.SetErrMsgFromBytes(resp.Content);
                return r;
            }

            profile.Content = resp.Content;
            profile.ContentType = resp.ContentType;
            profile.Etag = resp.Etag;

            r.Success = true;
            r.Content = profile;

            return r;
        }

        public async Task<LrsResponse> SaveAgentProfileAsync(AgentProfileDocument profile)
        {
            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "profileId", profile.Id },
                { "agent", profile.Agent.ToJson(Version) }
            };

            return await SaveDocument("agents/profile", queryParams, profile);
        }

        public async Task<LrsResponse> DeleteAgentProfileAsync(AgentProfileDocument profile)
        {
            Dictionary<string, string> queryParams = new Dictionary<string, string>
            {
                { "profileId", profile.Id },
                { "agent", profile.Agent.ToJson(Version) }
            };
            // TODO: need to pass Etag?

            return await DeleteDocument("agents/profile", queryParams);
        }
    }
}