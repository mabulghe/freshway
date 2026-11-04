using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.ServiceProcess;
using System.Text;
using System.Threading;

// ============================================================
// FreshWayGateway.cs  v1.0
//
// Reemplaza SOLO el gateway.jar (Spring Boot) de Mettler.
// Los backends Java (MettlerWSV1.1_G.jar y MettlerWSV1.1_B.jar)
// CONTINUAN corriendo porque son los que manejan el hardware USB.
//
// Arquitectura:
//   Cache :8082 → FreshWayGateway (C#, instantaneo)
//                    ├→ MettlerWSV1.1_G.jar :8083  (LabelPrinter-1)
//                    └→ MettlerWSV1.1_B.jar :8084  (ReceiptPrinter-1)
//
// Ganancia: elimina los ~12-18 segundos de startup de Spring Boot
// y la latencia de la JVM en cada request.
//
// Compilar:
//   "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
//   /target:exe /platform:x64
//   /r:System.Web.dll /r:System.ServiceProcess.dll
//   /out:FreshWayGateway.exe FreshWayGateway.cs
//
// Modo consola: FreshWayGateway.exe --console
// ============================================================

namespace FreshWayGateway
{
    static class Program
    {
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--console")
            {
                GatewayService svc = new GatewayService();
                svc.StartService();
                Console.WriteLine("Gateway corriendo. Enter para detener.");
                Console.ReadLine();
                svc.StopService();
            }
            else
            {
                ServiceBase.Run(new GatewayService());
            }
        }
    }

    public class GatewayService : ServiceBase
    {
        private HttpProxy _proxy;

        public GatewayService() { ServiceName = "FreshWayGateway"; }

        protected override void OnStart(string[] args) { StartService(); }
        protected override void OnStop()               { StopService();  }

        public void StartService()
        {
            // Leer configuracion desde gateway.ini (mismo directorio que el exe)
            int listenPort   = 8082;
            int portGreen    = 8083;   // MettlerWSV1.1_G.jar  (Label)
            int portBlue     = 8084;   // MettlerWSV1.1_B.jar  (Receipt/Linerless)
            int backendTimeout = 30000; // ms - igual al timeout del OPOS driver

            string ini = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location),
                "gateway.ini");

            if (File.Exists(ini))
            {
                foreach (string line in File.ReadAllLines(ini))
                {
                    string l = line.Trim();
                    if (l.StartsWith(";") || !l.Contains("=")) continue;
                    string key = l.Split('=')[0].Trim().ToLower();
                    string val = l.Split(new char[]{'='}, 2)[1].Trim();
                    int v;
                    switch (key)
                    {
                        case "port":            if (int.TryParse(val, out v)) listenPort   = v; break;
                        case "port_green":      if (int.TryParse(val, out v)) portGreen    = v; break;
                        case "port_blue":       if (int.TryParse(val, out v)) portBlue     = v; break;
                        case "backend_timeout": if (int.TryParse(val, out v)) backendTimeout = v; break;
                    }
                }
            }

            // Mapa device → puerto backend
            // Ajustar si el JAR usa una logica diferente
            Dictionary<string, int> routing = new Dictionary<string, int>
            {
                { "LabelPrinter-1",       portGreen },
                { "LabelPrinter-1-Sim",   portGreen },
                { "ReceiptPrinter-1",     portBlue  },
                { "LinerlessPrinter-1",   portBlue  },
            };

            _proxy = new HttpProxy(listenPort, routing, backendTimeout);
            _proxy.Start();
        }

        public void StopService() { if (_proxy != null) _proxy.Stop(); }
    }

    // --------------------------------------------------------
    // HTTP Proxy: escucha en listenPort, rutea al backend
    // correcto segun el device name visto en p_open.
    // --------------------------------------------------------
    class HttpProxy
    {
        private readonly HttpListener   _listener;
        private readonly Dictionary<string, int> _routing;
        private readonly int            _backendTimeout;
        private Thread        _thread;
        private volatile bool _running;

        // Puerto actual del backend — se actualiza en cada p_open
        private int  _currentPort;
        private readonly object _lock = new object();

        public HttpProxy(int port, Dictionary<string, int> routing, int backendTimeout)
        {
            _routing        = routing;
            _backendTimeout = backendTimeout;

            // Default: primer valor del mapa (Green/Label)
            foreach (int p in routing.Values) { _currentPort = p; break; }

            _listener = new HttpListener();
            _listener.Prefixes.Add("http://+:" + port + "/");
        }

        public void Start()
        {
            _listener.Start();
            _running = true;
            _thread  = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "GatewayLoop";
            _thread.Start();

            Log("==============================================");
            Log(" FreshWay Gateway v1.0");
            Log(" Escuchando en   : " + _listener.Prefixes);
            Log(" Green (Label)   : " + BackendPort("LabelPrinter-1"));
            Log(" Blue (Receipt)  : " + BackendPort("ReceiptPrinter-1"));
            Log("==============================================");
        }

        public void Stop()
        {
            _running = false;
            _listener.Stop();
        }

        private int BackendPort(string dev)
        {
            int p;
            return _routing.TryGetValue(dev, out p) ? p : _currentPort;
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    HttpListenerContext ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(delegate(object state) {
                        Handle((HttpListenerContext)state);
                    }, ctx);
                }
                catch (HttpListenerException) { if (!_running) break; }
                catch (Exception ex)          { Log("Loop ERR: " + ex.Message); }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            //string rawPath = ctx.Request.Url.AbsolutePath;
            //string rawQuery = ctx.Request.Url.Query ?? "";
            // RawUrl contiene la ruta original + query string SIN decodificar caracteres
            string rawPath = ctx.Request.RawUrl;

            // Extraer el metodo para ver si es p_open
            // URL: /pos/p_open/LabelPrinter-1
            // Aislar la ruta pura (sin ?) para extraer method y device en p_open
            string pathOnly = rawPath.Split('?')[0];
            string[] parts = pathOnly.TrimStart('/').Split(new char[]{'/'}, 3);
            string method = parts.Length >= 2 ? parts[1].ToLower() : "";

            int targetPort;

            lock (_lock)
            {
                if (method == "p_open" && parts.Length >= 3)
                {
                    // Extraer device name (puede tener encoding URL)
                    string dev = Uri.UnescapeDataString(parts[2]).Trim('/');
                    if (_routing.ContainsKey(dev))
                    {
                        _currentPort = _routing[dev];
                        Log("[ROUTE] " + dev + " → :" + _currentPort);
                    }
                    else
                    {
                        Log("[ROUTE] Device desconocido: " + dev + " → usando puerto actual :" + _currentPort);
                    }
                }
                targetPort = _currentPort;
            }

            // Reenviar la request al backend
            string backendUrl = "http://localhost:" + targetPort + rawPath; // + rawQuery;
            string response   = "";
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(backendUrl);
                req.Method  = ctx.Request.HttpMethod;
                req.Timeout = _backendTimeout;
                req.KeepAlive = false;

                // Copiar headers relevantes
                if (ctx.Request.ContentLength64 > 0)
                {
                    req.ContentType   = ctx.Request.ContentType;
                    req.ContentLength = ctx.Request.ContentLength64;
                    byte[] body = new byte[ctx.Request.ContentLength64];
                    ctx.Request.InputStream.Read(body, 0, body.Length);
                    req.GetRequestStream().Write(body, 0, body.Length);
                }

                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    response = sr.ReadToEnd();
                }
            }
            catch (WebException wex)
            {
                if (wex.Response != null)
                {
                    using (StreamReader sr = new StreamReader(wex.Response.GetResponseStream()))
                        response = sr.ReadToEnd();
                }
                else
                {
                    response = "ERR:" + wex.Message;
                    Log("[ERR] Backend :" + targetPort + " " + rawPath + " → " + wex.Message);
                }
            }
            catch (Exception ex)
            {
                response = "ERR:" + ex.Message;
                Log("[ERR] " + rawPath + " → " + ex.Message);
            }

            // Log compacto
            Log("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] :"
                + targetPort + "  "
                + method.PadRight(24)
                + " | " + Trunc(parts.Length >= 3 ? parts[2] : "", 35).PadRight(35)
                + " | " + Trunc(response, 20));

            // Devolver respuesta a Cache
            byte[] buf = Encoding.UTF8.GetBytes(response);
            ctx.Response.ContentLength64 = buf.Length;
            ctx.Response.ContentType     = "text/plain; charset=utf-8";
            ctx.Response.OutputStream.Write(buf, 0, buf.Length);
            ctx.Response.Close();
        }

        private static void Log(string msg) { Console.WriteLine(msg); }

        private static string Trunc(string s, int max)
        {
            if (s == null) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }
    }
}
