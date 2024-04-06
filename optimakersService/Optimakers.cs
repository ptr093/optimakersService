using Newtonsoft.Json;
using NLog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.FtpClient;
using System.Net.Http;
using System.Net.Http.Headers;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace optimakersService
{
    public partial class Optimakers : ServiceBase
    {
        private static Logger logger = LogManager.GetCurrentClassLogger();
        private Timer tService;
        private string mode;
        private DateTime scheduledTime;
        private int dueTime;
        TimeSpan timeSpan;

        public Optimakers()
        {
            InitializeComponent();
        }


        private async void OnSync_ExecuteSynchro(object e,string server, string username,string password,string remoteDir,string localDir
            , string dbServer, string dbFile, string dbUsername, string dbPassword, string apiUrl, string apiPass, string ApiUserName)
        {
            try
            {
                logger.Info("Rozpoczynam migrację danych z systemu magazynowego do systemu planowania produkcji");
               
               await ProcessMigration(server, username, password, remoteDir,localDir,
                      dbServer,  dbFile,  dbUsername,  dbPassword,  apiUrl,  apiPass,  ApiUserName);
            }
            catch (Exception ex)
            {
                logger.Error("Błąd migracji danych: {0}", ex.Message);

            }
       
        }
        static async Task<bool> InsertDataAsync(string url, DataTable dataTable)
        {
            string jsonData = JsonConvert.SerializeObject(dataTable);

            using (HttpClient client = new HttpClient())
            {
                StringContent content = new StringContent(jsonData, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await client.PostAsync(url, content);

                return response.IsSuccessStatusCode;
            }
        }
        static async Task<bool> InsertDataAsync(string url, string jsonData)
        {
         

            using (HttpClient client = new HttpClient())
            {
                StringContent content = new StringContent(jsonData, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await client.PostAsync(url, content);

                return response.IsSuccessStatusCode;
            }
        }
        static async Task<bool> InsertDataAsync(string url, DataTable dataTable, string username, string password)
        {
            string jsonData = JsonConvert.SerializeObject(dataTable); // Serialize  DataTable to JSON

            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($"{username}:{password}")));

                StringContent content = new StringContent(jsonData, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await client.PostAsync(url, content);

                return response.IsSuccessStatusCode;
            }
        }




        static DataTable GetDokMag(string connectionString, string numerZamowienia)
        {
            string query = "SELECT * FROM DokumentyMagazynowe WHERE Typ = 'Wydanie surowców do zamówienia' AND NumerZamowienia = @NumerZamowienia";

            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    connection.Open();

                    using (SqlCommand command = new SqlCommand(query, connection))
                    {
                        command.Parameters.AddWithValue("@NumerZamowienia", numerZamowienia);

                        SqlDataAdapter adapter = new SqlDataAdapter(command);
                        DataTable table = new DataTable();
                        adapter.Fill(table);

                        if (table.Rows.Count > 0)
                        {
                            return table;
                        }
                        else
                        {
                            logger.Info($"Nie znaleziono dokumentu magazynowego dla numeru zamówienia '{numerZamowienia}'.");
                            return null;
                        }
                    }
                }
            }
            catch (SqlException ex)
            {
                logger.Info($"Wystąpił błąd SQL: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                logger.Info($"Wystąpił nieoczekiwany błąd: {ex.Message}");
                return null;
            }
        }

      
        static async Task ProcessMigration(string server, string username, string password, string remoteDir, string localDir,
            string dbServer,string dbFile,string dbUsername,string dbPassword,string apiUrl,string apiPass,string ApiUserName)
        {
            password = PasswordHasher.DecryptPassword(password);
         
            var connectionString = MakeConnectionString(dbServer, dbFile, true, dbUsername, dbPassword);
            using (FtpClient ftp = new FtpClient())
            {
                ftp.Host = server;
                ftp.Credentials = new NetworkCredential(username, password);

                try
                {
                    ftp.Connect();
                    logger.Info("Połączono z serwerem FTP");

                    ftp.SetWorkingDirectory(remoteDir);

                    foreach (FtpListItem item in ftp.GetListing())
                    {
                        if (item.Type == FtpFileSystemObjectType.File && item.Name.EndsWith(".json"))
                        {
                            string remoteFilePath = item.FullName;
                            string localFilePath = Path.Combine(localDir, item.Name);

                            logger.Info($"Pobieranie pliku {remoteFilePath}");

                            using (Stream remoteFileStream = ftp.OpenRead(remoteFilePath))
                            {
                                using (FileStream localFileStream = File.Create(localFilePath))
                                {
                                    remoteFileStream.CopyTo(localFileStream);
                                }
                            }

                            logger.Info($"Plik {remoteFilePath} pobrany i zapisany lokalnie jako {localFilePath}");
                     
                       
                            string jsonContent = File.ReadAllText(localFilePath);

                            dynamic config = JsonConvert.DeserializeObject(jsonContent);
                         
                            DataTable getDataFromDb = GetDokMag(connectionString, (string)config.numer_zamowienia);
                            if (getDataFromDb != null)
                            {

                                bool success = await InsertDataAsync(apiUrl, getDataFromDb);
                                // JsonFile
                                //bool success = await InsertDataAsync(apiUrl, jsonContent);

                                if (success)
                                {
                                    ftp.DeleteFile(remoteFilePath);
                                    logger.Info($"Plik {remoteFilePath} usunięty z serwera FTP");

                                    // The functionality that moves a completed file to another folder.
                                   /* string destinationFolder = remoteDir + "/test2"; // Ścieżka na serwerze FTP
                                    string destinationFilePath = destinationFolder + "/" + item.Name;
                                    ftp.Rename(remoteFilePath, destinationFilePath); // Przeniesienie pliku na serwerze FTP
                                    logger.Info($"Plik {remoteFilePath} został przeniesiony z serwera FTP");
                                   */
                                }
                                else
                                {
                                    logger.Info("Nie udało się wykonać metody POST");
                                }

                            }
                        }
                    }

                    logger.Info("Operacja zakończona pomyślnie");
                }
                catch (Exception ex)
                {
                    logger.Info($"Wystąpił błąd: {ex.Message}");
                }
                finally
                {
                    if (ftp.IsConnected)
                        ftp.Disconnect();
                }
            }
        }
        private static string MakeConnectionString(string dataSource, string initialCatalog, bool useLoginPassword, string userID, string password, int timeout = 10)
        {
            string connectionString = string.Empty;

            if (useLoginPassword)
            {
                connectionString = string.Format("Data Source={0};Initial Catalog={1};User ID={2};Password={3};Connect Timeout={4};", dataSource, initialCatalog, userID, password, timeout);
            }
            else
            {
                connectionString = string.Format("Data Source={0}; Initial Catalog={1}; Integrated Security=True;Connect Timeout={2};", dataSource, initialCatalog, timeout);
            }

            return connectionString;
        }
        static void ProcessJsonFile(string ms)
        {
            logger.Info("test");
        }

   
        protected override void OnStart(string[] args)
        {
            logger.Info("Uruchomienie usługi");
            string configFileName = "config.json";
            string configFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configFileName);
          
            if (File.Exists(configFilePath))
            {
                logger.Info("Wczytywanie pliku konfiguracyjnego usługi..");
                string jsonContent = File.ReadAllText(configFilePath);

                dynamic config = JsonConvert.DeserializeObject(jsonContent);
             

                tService = new Timer(new TimerCallback((sender) => OnSync_ExecuteSynchro(sender,(String)config.FTP_SERVER, (String)config.FTP_SERVER_LOGIN,
                   (String)config.FTP_SERVER_PASSWORD, (String)config.FTP_DIRECTORY, (String)config.LOCAL_DIRECTORY_PATH,
                     (String)config.DB_SERVER, (String)config.DB_FILE, (String)config.DB_USERNAME,
                     (String)config.DB_PASSWORD, (String)config.API_URL, (String)config.API_PASSWORD, (String)config.API_USERNAME
                   )));

                scheduledTime = DateTime.MinValue;
                mode = config.Mode;
           
                if (mode.Equals("Daily"))
                {
                    scheduledTime = DateTime.Parse(config.ScheduledTime);
                    if (DateTime.Now > scheduledTime)
                    {
                        scheduledTime = scheduledTime.AddDays(1);
                    }
                    logger.Info(String.Format("Rozpoczęcie synchronizacji 1 dziennie zawsze o godzinie {0}", scheduledTime));

                    this.timeSpan = scheduledTime.Subtract(DateTime.Now);
                    //Get the difference in Minutes between the Scheduled and Current Time.
                    this.dueTime = Convert.ToInt32(this.timeSpan.TotalMilliseconds);

                    //Change the Timer's Due Time.

                    tService.Change(this.dueTime, 24 * 60 * 60 * 1000);//Timeout.Infinite);
                }
                if (mode == "Interval")
                {
                    //Get the Interval in Minutes from AppSettings.
                    int intervalMinutes = Convert.ToInt32(config.Interval_Minutes);

                    //Set the Scheduled Time by adding the Interval to Current Time.
                    scheduledTime = DateTime.Now.AddMinutes(intervalMinutes);
                    if (DateTime.Now > scheduledTime)
                    {
          
                        scheduledTime = scheduledTime.AddMinutes(intervalMinutes);
                    }
                    logger.Info("Synchronizacja włączona. Synchronizacja o ustalony czas w minutach: {0} ", scheduledTime);

                    TimeSpan timeSpan = scheduledTime.Subtract(DateTime.Now);
            
                    int dueTime = Convert.ToInt32(timeSpan.TotalMilliseconds);
                    tService.Change(dueTime, intervalMinutes * 60 * 1000);
                }
            }
            else
            {
                logger.Info("Nie znaleziono pliku konfiguracyjnego");
            }

        }
          



        protected override void OnStop()
        {
            logger.Info("Usługa została zatrzymana");
        }
    }
}
