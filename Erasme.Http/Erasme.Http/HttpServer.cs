// HttpServer.cs
// 
//  HTTP server that handle HTTP requests and WebSocket request. 
//  A HTTP server process a request with the given IHttpHandler(s).
//  This class can be overrided to change the default behaviour.
//
// Author(s):
//  Daniel Lacroix <dlacroix@erasme.org>
// 
// Copyright (c) 2013-2015 Departement du Rhone - Metropole de Lyon
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//

using System;
using System.Net;
using System.Reflection;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Erasme.Http
{
	public class HttpServer
	{
		// server certificate if the connexion is secure
		X509Certificate serverCertificate = null;

		Socket listener;
		string name;
		bool started = false;
		List<IHttpHandler> handlers = new List<IHttpHandler>();
		BufferManager<HttpServerClient> clientsPool = new BufferManager<HttpServerClient>();

		object instanceLock = new object();
		LinkedList<HttpServerClient> clients = new LinkedList<HttpServerClient>();

		public HttpServer(int port): this(port, (X509Certificate)null)
		{
		}

		public HttpServer(int port, string certificateFile, string certificatePassword)
		{
			X509Certificate certificate = null;
			if(certificateFile != null)
				certificate = new X509Certificate2(certificateFile, certificatePassword);

			Init (port, certificate);
		}

		public HttpServer(int port, X509Certificate certificate)
		{
			Init(port, certificate);
		}

		public X509Certificate ServerCertificate {
			get {
				return serverCertificate;
			}
			set {
				serverCertificate = value;
			}
		}

		public void LoadServerCertificate(string file, string password)
		{
			if(file != null)
				ServerCertificate = new X509Certificate2(file, password);
		}

		void Init(int port, X509Certificate certificate)
		{
			serverCertificate = certificate;

			listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.IP);
			IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, port);
			listener.Bind(endPoint);
			listener.Listen(1024);
			// set a default server name
			name = (Assembly.GetEntryAssembly().GetName().Name)+" (v"+Assembly.GetEntryAssembly().GetName().Version+")";
			StopOnException = false;
			AllowGZip = true;
			KeepAliveMax = 100;
			KeepAliveTimeout = 10;
		}

		public string ServerName {
			get {
				return name;
			}
			set {
				name = value;
			}
		}

		/// <summary>
		/// Gets or sets a value indicating whether this HTTP server allow GZip
		/// compression if the HTTP client support it
		/// </summary>
		/// <value><c>true</c> if allow GZip; otherwise, <c>false</c>.</value>
		public bool AllowGZip { get; set; }

		public int KeepAliveMax { get; set; }

		public int KeepAliveTimeout { get; set; }

		public Task RunAsync(bool disposeHandlers = false)
		{
			return null;
		}

		public void Run(bool disposeHandlers = false)
		{
			RunAsync(disposeHandlers).Wait();
		}

		public void Start()
		{
			started = true;
			StartAccept();
		}

		void StartAccept()
		{
			SocketAsyncEventArgs e = new SocketAsyncEventArgs();
			e.Completed += AcceptCallback;
			if(!listener.AcceptAsync(e))
				AcceptCallback(listener, e);
		}

		async void AcceptCallback(object sender, SocketAsyncEventArgs e)
		{
			if(e.SocketError == SocketError.Success) {
				StartAccept();

				HttpServerClient client;
				LinkedListNode<HttpServerClient> clientNode = clientsPool.Get();
				if(clientNode == null) {
					client = new HttpServerClient(this, e.AcceptSocket);
					client.KeepAliveCountdown = KeepAliveMax;
					client.KeepAliveTimeout = KeepAliveTimeout;
					clientNode = new LinkedListNode<HttpServerClient>(client);
				}
				else {
					client = clientNode.Value;
					client.Reset(e.AcceptSocket);
					client.KeepAliveCountdown = KeepAliveMax;
					client.KeepAliveTimeout = KeepAliveTimeout;
				}

				lock(instanceLock) {
					clients.AddFirst(clientNode);
				}
				try {
					await client.ProcessAsync();
				}
				catch(Exception exception) {
					OnServerException(exception);
				}
				finally {
					lock(instanceLock) {
						clients.Remove(clientNode);
					}
					clientsPool.Release(clientNode);
				}
			}
		}

		public IEnumerable<HttpServerClient> Clients
		{
			get {
				lock(instanceLock) {
					return new List<HttpServerClient>(clients);
				}
			}
		}

		protected virtual void OnServerException(Exception exception)
		{
			Console.WriteLine(exception.ToString());
		}

		protected internal virtual async Task ProcessRequestAsync(HttpContext context)
		{
			Exception exception = null;
			try {
				// Process the request with each HTTP handler
				foreach(IHttpHandler handler in handlers) {
					await handler.ProcessRequestAsync(context);
				}
			}
			catch(Exception e) {
				exception = e;
			}
			if(exception != null) {
				OnProcessRequestError(context, exception);
			}
		}

		public bool StopOnException { get; set; }

		protected virtual void OnProcessRequestError(HttpContext context, Exception exception)
		{
			try {
				if((context.Response != null) && (context.Response.Content != null))
					context.Response.Content.Dispose();
			}
			catch(Exception) {
			}
			// return an internal server error
			context.Response.StatusCode = 500;
			context.Response.Content = HttpContent.Null;
			if(StopOnException) {
				Console.WriteLine("Server Exception: "+exception.ToString());
				Stop();
				Environment.Exit(2);
			}
		}

		protected internal virtual void OnWebSocketHandlerOpen(WebSocketHandler handler)
		{
			handler.OnOpen();
		}

		protected internal virtual void OnWebSocketHandlerClose(WebSocketHandler handler)
		{
			handler.OnClose();
		}

		protected internal virtual void OnWebSocketHandlerMessage(WebSocketHandler handler, string message)
		{
			handler.OnMessage(message);
		}

		protected internal virtual void OnWebSocketHandlerMessage(WebSocketHandler handler, byte[] message)
		{
			handler.OnMessage(message);
		}

		protected internal virtual void WebSocketHandlerSend(WebSocketHandler handler, string message)
		{
			handler.SendInternal(message);
		}

		protected internal virtual void WebSocketHandlerSend(WebSocketHandler handler, byte[] message)
		{
			handler.SendInternal(message);
		}

		public void Stop()
		{
			Stop(false);
		}

		public void Stop(bool disposeHandlers)
		{
			listener.Close();
			if(disposeHandlers) {
				foreach(IHttpHandler handler in handlers) {
					IDisposable disposable = handler as IDisposable;
					if(disposable != null) {
						try {
							disposable.Dispose();
						}
						catch(Exception) {}
					}
				}
			}
		}

		public void Add(IHttpHandler handler)
		{
			if(started)
				throw new Exception("Cant add IHttpHandler when the HttpServer is started");
			handlers.Add(handler);
		}
	}
}

