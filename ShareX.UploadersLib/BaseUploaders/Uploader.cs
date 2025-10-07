#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2025 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Text;

namespace ShareX.UploadersLib
{
    public class Uploader
    {
        public bool IsUploading { get; protected set; }
        public UploaderErrorManager Errors { get; private set; } = new UploaderErrorManager();

        public int BufferSize { get; set; } = 8192;

        protected bool StopUploadRequested { get; set; }

        protected bool ReturnResponseOnError { get; set; }

        protected ResponseInfo LastResponseInfo { get; set; }

        protected UploadResult SendRequestFile(string url, Stream data, string fileName, string fileFormName, Dictionary<string, string> args = null,
            NameValueCollection headers = null, CookieCollection cookies = null, HttpMethod method = HttpMethod.POST, string contentType = RequestHelpers.ContentTypeMultipartFormData,
            string relatedData = null)
        {
            UploadResult result = new UploadResult();

            IsUploading = true;
            StopUploadRequested = false;

            try
            {
                string boundary = RequestHelpers.CreateBoundary();
                contentType += "; boundary=" + boundary;

                byte[] bytesArguments = RequestHelpers.MakeInputContent(boundary, args, false);
                byte[] bytesDataOpen;

                if (relatedData != null)
                {
                    bytesDataOpen = RequestHelpers.MakeRelatedFileInputContentOpen(boundary, "application/json; charset=UTF-8", relatedData, fileName);
                }
                else
                {
                    bytesDataOpen = RequestHelpers.MakeFileInputContentOpen(boundary, fileFormName, fileName);
                }

                byte[] bytesDataClose = RequestHelpers.MakeFileInputContentClose(boundary);

                long contentLength = bytesArguments.Length + bytesDataOpen.Length + data.Length + bytesDataClose.Length;

                HttpWebRequest request = CreateWebRequest(method, url, headers, cookies, contentType, contentLength);

                using (Stream requestStream = request.GetRequestStream())
                {
                    requestStream.Write(bytesArguments, 0, bytesArguments.Length);
                    requestStream.Write(bytesDataOpen, 0, bytesDataOpen.Length);
                    if (!TransferData(data, requestStream)) return null;
                    requestStream.Write(bytesDataClose, 0, bytesDataClose.Length);
                }

                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    result.ResponseInfo = ProcessWebResponse(response);
                    result.Response = result.ResponseInfo?.ResponseText;
                }

                result.IsSuccess = true;
            }
            catch (Exception e)
            {
                if (!StopUploadRequested)
                {
                    string response = ProcessError(e, url);

                    if (ReturnResponseOnError && e is WebException)
                    {
                        result.Response = response;
                    }
                }
            }
            finally
            {
                IsUploading = false;
            }

            return result;
        }
        #region Helper methods

        protected bool TransferData(Stream dataStream, Stream requestStream, long dataPosition = 0, long dataLength = -1)
        {
            if (dataPosition >= dataStream.Length)
            {
                return true;
            }

            if (dataStream.CanSeek)
            {
                dataStream.Position = dataPosition;
            }

            if (dataLength == -1)
            {
                dataLength = dataStream.Length;
            }
            dataLength = Math.Min(dataLength, dataStream.Length - dataPosition);

            int length = (int)Math.Min(BufferSize, dataLength);
            byte[] buffer = new byte[length];
            int bytesRead;

            long bytesRemaining = dataLength;
            while (!StopUploadRequested && (bytesRead = dataStream.Read(buffer, 0, length)) > 0)
            {
                requestStream.Write(buffer, 0, bytesRead);
                bytesRemaining -= bytesRead;
                length = (int)Math.Min(buffer.Length, bytesRemaining);
            }

            return !StopUploadRequested;
        }

        private string ProcessError(Exception e, string requestURL)
        {
            string responseText = null;

            if (e != null)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("Error message:");
                sb.AppendLine(e.Message);

                if (!string.IsNullOrEmpty(requestURL))
                {
                    sb.AppendLine();
                    sb.AppendLine("Request URL:");
                    sb.AppendLine(requestURL);
                }

                if (e is WebException webException)
                {
                    try
                    {
                        using (HttpWebResponse webResponse = (HttpWebResponse)webException.Response)
                        {
                            ResponseInfo responseInfo = ProcessWebResponse(webResponse);

                            if (responseInfo != null)
                            {
                                responseText = responseInfo.ResponseText;

                                sb.AppendLine();
                                sb.AppendLine("Status code:");
                                sb.AppendLine($"({(int)responseInfo.StatusCode}) {responseInfo.StatusDescription}");

                                if (!string.IsNullOrEmpty(requestURL) && !requestURL.Equals(responseInfo.ResponseURL))
                                {
                                    sb.AppendLine();
                                    sb.AppendLine("Response URL:");
                                    sb.AppendLine(responseInfo.ResponseURL);
                                }

                                if (responseInfo.Headers != null)
                                {
                                    sb.AppendLine();
                                    sb.AppendLine("Headers:");
                                    sb.AppendLine(responseInfo.Headers.ToString().TrimEnd());
                                }

                                sb.AppendLine();
                                sb.AppendLine("Response text:");
                                sb.AppendLine(responseInfo.ResponseText);
                            }
                        }
                    }
                    catch (Exception nested)
                    {
                        // DebugHelper.WriteException(nested, "ProcessError() WebException handler");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("Stack trace:");
                sb.Append(e.StackTrace);

                string errorText = sb.ToString();

                if (Errors == null) Errors = new UploaderErrorManager();
                Errors.Add(errorText);

                // DebugHelper.WriteLine("Error:\r\n" + errorText);
            }

            return responseText;
        }

        private HttpWebRequest CreateWebRequest(HttpMethod method, string url, NameValueCollection headers = null, CookieCollection cookies = null,
            string contentType = null, long contentLength = 0)
        {
            LastResponseInfo = null;

            HttpWebRequest request = RequestHelpers.CreateWebRequest(method, url, headers, cookies, contentType, contentLength);
            return request;
        }

        private ResponseInfo ProcessWebResponse(HttpWebResponse response)
        {
            if (response != null)
            {
                ResponseInfo responseInfo = new ResponseInfo()
                {
                    StatusCode = response.StatusCode,
                    StatusDescription = response.StatusDescription,
                    ResponseURL = response.ResponseUri.OriginalString,
                    Headers = response.Headers
                };

                using (Stream responseStream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(responseStream, Encoding.UTF8))
                {
                    responseInfo.ResponseText = reader.ReadToEnd();
                }

                LastResponseInfo = responseInfo;

                return responseInfo;
            }

            return null;
        }

        #endregion Helper methods
    }
}