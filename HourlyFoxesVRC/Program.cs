using System;
using File = System.IO.File;
using System.Timers;

using Newtonsoft.Json.Linq;

using VRChat.API.Api;
using VRChat.API.Client;
using VRChat.API.Model;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Net.Http.Headers;
using System.Drawing;
using System.Diagnostics;
using HttpMethod = System.Net.Http.HttpMethod;
using System.Web;

namespace HourlyFoxesVRC
{
    internal class HourlyFoxesVRC
    {
        // User-Agent stuff
        private const string GlobalUserAgentAppName = "HourlyFoxesVRC";
        private const string GlobalUserAgentAppVers = "1.0.0";
        private string? m_contactInfo = "";
        private string m_globalUserAgent;
        private string m_encodedGlobalUserAgent;

        // File Names
        // TODO: I coulddddd make this a .json config file but I'mmmmmmm lazy rn, maybe later
        private const string ContactFileName = "contactInfo.txt";
        private const string TokenFileName = "token.txt";
        private const string PreviousFileIDFileName = "previousFileID.txt";
        private const string GroupIDFileName = "groupID.txt";
        private const string MessagesFileName = "messages.txt";
        private const string StartupImageFile = "startup.png";
        private const string StopErrorImageFile = "stop.png";
        private const string ManualEndImageFile = "manual stop.png";

        // VRC Api
        private string? m_groupID = ""; // Group for posting
        private Configuration? m_config;
        private AuthenticationApi? m_authApi;
        private GroupsApi? m_groupsApi;
        private UsersApi? m_userApi;
        private FilesApi? m_filesApi;
        private string m_currentToken; // Login token

        // Timing
        private System.Timers.Timer? m_timer; // Timer
        private int m_postHour; // When was the last post done

        //Other
        private const string AutomatedSuffix = " [automated]"; // Added to the end of all announcements
        private string[] m_possibleMessages = new string[0]; // Holds the messages for random
        private string m_lastFileId = null; // File ID to delete when new post is made
        private Random m_rand = new Random(); // For message Selection

        static void Main(string[] args)
        {
            HourlyFoxesVRC hourlyFoxesVRCApi = new HourlyFoxesVRC();
            hourlyFoxesVRCApi.Start();
        }

        public void Start()
        {
            // Load stuff
            if (File.Exists(ContactFileName)) { m_contactInfo = File.ReadAllText(ContactFileName); }
            if (m_contactInfo.Trim().Length == 0)
            {
                // Make file
                bool correct = false;
                while ((m_contactInfo == null || m_contactInfo.Trim().Length == 0) && !correct)
                {
                    Console.WriteLine("Please input an email address (or some sort of contact info) in case VRChat needs to contact you: ");
                    m_contactInfo = Console.ReadLine();
                    correct = YesNoPrompt($"Are you sure \"{m_contactInfo}\" is a way to contact you? (Y/N)");
                }
                File.WriteAllText(ContactFileName, m_contactInfo);
            }

            if (File.Exists(GroupIDFileName)) { m_groupID = File.ReadAllText(GroupIDFileName); }
            if (m_groupID.Trim().Length == 0 || (!m_groupID.StartsWith("grp_")))
            {
                // Make file
                bool correct = false;
                while ((m_groupID == null || m_groupID.Trim().Length == 0) && !correct && !m_groupID.StartsWith("grp_"))
                {
                    Console.WriteLine("Please input a group id for the announcements: ");
                    m_groupID = Console.ReadLine();

                    if (!m_groupID.StartsWith("grp_")) { AskLogoutAndExit("That is not a valid group ID.", true); }

                    correct = YesNoPrompt($"Are you sure \"{m_groupID}\" is a valid group id? (Y/N)");
                }
                File.WriteAllText(GroupIDFileName, m_groupID);
            }

            if (File.Exists(MessagesFileName)) { m_possibleMessages = File.ReadAllText(MessagesFileName).Split("\n"); }
            else { AskLogoutAndExit($"{MessagesFileName} is missing...", true); }

            if (File.Exists(PreviousFileIDFileName)) { m_lastFileId = File.ReadAllText(PreviousFileIDFileName); }

            m_globalUserAgent = $"{GlobalUserAgentAppName}/{GlobalUserAgentAppVers} {m_contactInfo}";
            m_encodedGlobalUserAgent = HttpUtility.UrlEncode(m_globalUserAgent);
            Console.WriteLine($"Set User-Agent to \"{m_encodedGlobalUserAgent}\"");

            // Authentication credentials
            m_config = new Configuration();
            m_config.UserAgent = m_encodedGlobalUserAgent;
            m_config.AddApiKey("apiKey", "JlE5Jldo5Jibnk5O5hTx6XVqsJu4WJ26");
            m_config.BasePath = @"https://api.vrchat.cloud/api/1";

            // Declare current user
            CurrentUser currentUser = null;

            // Attempt login with stored token else do a normal login
            string storedToken = "";
            if (File.Exists(TokenFileName))
            {
                storedToken = File.ReadAllText(TokenFileName);
                Console.WriteLine($"Attempting auth with stored token ({CensorString(storedToken, 11)})");
                currentUser = AuthenticateWithToken(storedToken);
            }
            else
            {
                currentUser = LoginWithUserAndPassword();
            }

            // Save cookies (auth)
            if (m_currentToken != storedToken)
            {
                Console.WriteLine($"Saving token ({CensorString(m_currentToken, 11)}) to {TokenFileName}");
                File.WriteAllText(TokenFileName, m_currentToken);
            }

            // Groups API
            m_groupsApi = new GroupsApi(m_config);

            // Logged in!
            Console.WriteLine($"Logged in as \"{currentUser.DisplayName}\"!");

            // Ctrl + C = Logout
            Console.WriteLine($"Press Ctrl+C to stop the bot.");
            Console.CancelKeyPress += delegate
            {
                if (YesNoPrompt("Post stop announcement? (Y/N)"))
                {
                    CreatePostFromImage(ManualEndImageFile, 
                        $"{GlobalUserAgentAppName} has manually stopped.{AutomatedSuffix}", 
                        $"Probably an update, it'll be back up soon.{AutomatedSuffix}", false);
                }
                AskLogoutAndExit("Ctrl+C pressed!");
            };

            try
            {
                CreatePostFromImage(StartupImageFile, $"{GlobalUserAgentAppName} has restarted!{AutomatedSuffix}", $"Enjoy the foxes soon!{AutomatedSuffix}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{ex.ToString()}");
                AskLogoutAndExit("An error has occured...");
            }
            
            //Check every 5 seconds
            m_timer = new System.Timers.Timer(5000);
            m_postHour = DateTime.Now.Hour - 1;
            m_timer.Elapsed += new ElapsedEventHandler(OnTimedEvent);
            m_timer.Start();

            while (true) { }
        }

        private void CreatePostFromImage(string file, string title, string text, bool notif = true)
        {
            Bitmap bitmap = (Bitmap)Bitmap.FromFile(file);
            bitmap = RescaleBitmapForVRC(bitmap);
            CreateHourlyFoxPost(BitmapToPngByteArray(bitmap), title, text, notif);
        }

        private CurrentUser AuthenticateWithToken(string token)
        {
            // Set token
            m_config.DefaultHeaders.Add("Cookie", "apiKey=" + m_config.ApiKey + ";" + "auth=" + token);
            m_config.AccessToken = token;

            // Auth
            m_authApi = new AuthenticationApi(m_config);
            VerifyAuthTokenResult res = m_authApi.VerifyAuthToken();
            if (!res.Ok) 
            { 
                Console.WriteLine("Couldn't authenticate with token (probably expired).");
                return LoginWithUserAndPassword();
            }
            m_currentToken = res.Token;
            return m_authApi.GetCurrentUser();
        }

        private CurrentUser LoginWithUserAndPassword()
        {
            // User and Password
            Console.WriteLine("Input Username: ");
            m_config.Username = Console.ReadLine();
            Console.WriteLine("Input Password: ");
            m_config.Password = PasswordInput();

            // Auth
            m_authApi = new AuthenticationApi(m_config);
            
            CurrentUser user = m_authApi.GetCurrentUser();

            //2fa just in case
            try
            {
                if (user == null)
                {
                    Console.WriteLine("\nInput 2FA Code: ");
                    string twoAuthCode = Console.ReadLine();
                    Verify2FAResult result = m_authApi.Verify2FA(new TwoFactorAuthCode(twoAuthCode));
                    m_authApi = new AuthenticationApi(m_config);
                }
            }
            catch (ApiException e)
            {
                Console.WriteLine("Exception when calling API: {0}", e.Message);
                Console.WriteLine("Status Code: {0}", e.ErrorCode);
                Console.WriteLine(e.ToString());
                Console.WriteLine(e.StackTrace);

                AskLogoutAndExit("Couldn't login.", true);
            }

            VerifyAuthTokenResult res = m_authApi.VerifyAuthToken();
            if (!res.Ok) { AskLogoutAndExit("Couldn't login.", true); }
            m_currentToken = res.Token;
            user = m_authApi.GetCurrentUser();
            return user;
        }

        private static object CensorString(string str, int shownCharacters)
        {
            char[] chars = str.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (i >= shownCharacters) { chars[i] = '*'; }
            }
            return new string(chars);
        }

        private static string PasswordInput()
        {
            var pass = string.Empty;
            ConsoleKey key;
            do
            {
                var keyInfo = Console.ReadKey(intercept: true);
                key = keyInfo.Key;

                if (key == ConsoleKey.Backspace && pass.Length > 0)
                {
                    Console.Write("\b \b");
                    pass = pass[0..^1];
                }
                else if (!char.IsControl(keyInfo.KeyChar))
                {
                    Console.Write("*");
                    pass += keyInfo.KeyChar;
                }
            } while (key != ConsoleKey.Enter);
            return pass;
        }

        private void AskLogoutAndExit(string message = null, bool skipLogout = false)
        {
            //Console
            if (message != null)
            {
                Console.WriteLine(message);
            }
            
            if (m_authApi == null)
            {
                Console.WriteLine("m_authApi is null, we aren't even logged in!");
            }
            else
            {
                if (!skipLogout)
                {
                    if (YesNoPrompt("Do you want to logout? (Y/N)")) { m_authApi?.Logout(); }
                }
            }
            
            //Exit app
            System.Environment.Exit(0);
        }

        private static bool YesNoPrompt(string message)
        {
            ConsoleKey response;
            do
            {
                Console.WriteLine(message);
                response = Console.ReadKey(false).Key;   // true is intercept key (dont show), false is show
                if (response != ConsoleKey.Enter)
                    Console.WriteLine();

            } while (response != ConsoleKey.Y && response != ConsoleKey.N);
            
            return response == ConsoleKey.Y;
        }

        private void OnTimedEvent(object source, ElapsedEventArgs e)
        {
            if (DateTime.Now.Minute == 0 && m_postHour != DateTime.Now.Hour)
            {
                m_postHour = DateTime.Now.Hour;
                try
                {
                    CreateHourlyFoxPost();
                }
                catch(Exception ex)
                {
                    Console.WriteLine($"{ex.ToString()}");
                    CreatePostFromImage(StopErrorImageFile, "The bot has stopped... [automated]", "Hopefully it'll be up soon. [automated]", false);
                    AskLogoutAndExit("An exception occured");
                }
            }
        }

        private void CreateHourlyFoxPost(byte[] customImage = null, string customTitle = null, string customText = null, bool sendNotification = true)
        {
            Console.WriteLine($"\n============================================================");
            Console.WriteLine($"Starting hourly fox post for {DateTime.Now.ToString("hh:mm:ss tt")}\n");

            // Make sure api is setup
            if (m_groupsApi == null) { AskLogoutAndExit("m_groupsApi is null..."); return; }

            // Setup Image
            byte[] pngBytes;
            if (customImage == null) { pngBytes = FoxImageToByteArray(); }
            else { pngBytes = customImage; } // Used for startup

            // Upload Image
            HttpResponseMessage galleryResponse = UploadPngToUserGallery(pngBytes);
            string? galleryResponseJson = galleryResponse.Content.ReadAsStringAsync().Result;
            Console.WriteLine(galleryResponseJson);
            string? fileId = (string?)JObject.Parse(galleryResponseJson)["id"];
            if (fileId == null) { AskLogoutAndExit($"Could not find file id..."); return; }

            // Setup Announcement
            string postUrl = @"https://vrchat.com/api/1/groups/"+ m_groupID +@"/announcement";
            string title = "A new hourly fox is available!" + AutomatedSuffix;
            string text = GetRandomMessage() + AutomatedSuffix;
            string imageId = fileId;
            
            // Apply overrides (used for startup & debugging)
            if (customTitle != null) { title = customTitle; }
            if (customText != null) { text = customText; }

            // Send out query
            CreateGroupAnnouncementRequest announcementRequest = new CreateGroupAnnouncementRequest(title, text, imageId, sendNotification);
            StringContent queryString = new StringContent(announcementRequest.ToJson(), Encoding.UTF8, "application/json");
            HttpResponseMessage responseMessage = ManuallyRequestApi(postUrl, queryString, HttpMethod.Post);
            string? responseJson = responseMessage.Content.ReadAsStringAsync().Result;
            Console.WriteLine(responseJson);

            // Delete old image.
            if (m_lastFileId != null && m_lastFileId.Trim().Length > 0)
            {
                HttpResponseMessage deleteResponseMessage = ManuallyRequestApi(@"https://vrchat.com/api/1/file/" + m_lastFileId, null, HttpMethod.Delete);
                string? deleteResponseJson = deleteResponseMessage.Content.ReadAsStringAsync().Result;
                Console.WriteLine(deleteResponseJson);
            }

            m_lastFileId = fileId;
            File.WriteAllText(PreviousFileIDFileName, fileId);
        }

        private byte[] FoxImageToByteArray()
        {
            // Get image URL
            string? imageURL = GetFoxImage(m_encodedGlobalUserAgent);
            if (imageURL == null) { AskLogoutAndExit("URL was null..."); return new byte[0]; }
            Console.WriteLine($"Downloading \"{imageURL}\"...");

            // Download fox image
            var client = new HttpClient();
            HttpRequestMessage request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, imageURL);
            request.Headers.Add("User-Agent", m_encodedGlobalUserAgent);
            HttpResponseMessage imgResponseMessage = client.Send(request);
            Stream imageBytes = imgResponseMessage.Content.ReadAsStream();

            // Load into bitmap
            Bitmap bitmap = (Bitmap)Bitmap.FromStream(imageBytes);
            if (bitmap == null) { return new byte[0]; }

            // Rescale so it can properly upload.
            bitmap = RescaleBitmapForVRC(bitmap);

            // Return png bytes
            return BitmapToPngByteArray(bitmap);
        }

        private static Bitmap RescaleBitmapForVRC(Bitmap bitmap)
        {
            int origWidth = bitmap.Width;
            int origHeight = bitmap.Height;

            // Rescale if needed
            if (bitmap.Width > 2048 || bitmap.Height > 2048)
            {
                float multiplier = 0;
                if (bitmap.Width > bitmap.Height) { multiplier = (float)bitmap.Width / 2048; }
                else { multiplier = (float)bitmap.Height / 2048; }

                bitmap = RescaleBitmap(bitmap, multiplier);
            }

            // Scale down by .75 till it's under 10mb (dumb but it works soooo)
            while (BitmapToPngByteArray(bitmap, false).Length > 1e+7)
            {
                bitmap = RescaleBitmap(bitmap, 0.75f);
            }

            int newWidth = bitmap.Width;
            int newHeight = bitmap.Height;

            if (origWidth != newWidth || newWidth != origHeight)
            {
                Console.WriteLine($"Scaled bitmap from {origWidth}x{origHeight} to {bitmap.Width}x{bitmap.Height}");
            }

            return bitmap;
        }

        private static Bitmap RescaleBitmap(Bitmap bitmap, float multiplier)
        {
            int newWidth = (int)Math.Floor(bitmap.Width * multiplier);
            int newHeight = (int)Math.Floor(bitmap.Height * multiplier);
            Bitmap ret = new Bitmap(bitmap, new Size(newWidth, newHeight));
            bitmap.Dispose();
            return ret;
        }

        private HttpResponseMessage UploadPngToUserGallery(byte[] data)
        {
            string postUrl = @"https://vrchat.com/api/1/gallery";
            MultipartContent multipart = new MultipartContent("form-data", "------WebKitFormBoundaryCjJ5d8TGAWHAjZBl"); //it expects multipart for whatever reason
            ByteArrayContent imageContent = new ByteArrayContent(data);
            imageContent.Headers.Add("Content-Disposition", "form-data; name=\"file\"; filename=\"blob\"");
            imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            multipart.Add(imageContent);
            return ManuallyRequestApi(postUrl, multipart, HttpMethod.Post);
        }

        private HttpResponseMessage ManuallyRequestApi(string postUrl, HttpContent content, HttpMethod method)
        {
            // Manually request
            HttpClient client = new HttpClient();
            HttpRequestMessage request;

            // Setup request
            request = new HttpRequestMessage(method, postUrl);
            request.Headers.Add("User-Agent", m_encodedGlobalUserAgent);
            Console.WriteLine($"\nRequesting \"{postUrl}\" with apikey: {m_config.ApiKey["apiKey"]}");
            request.Headers.Add("Cookie", "apiKey=" + m_config.ApiKey["apiKey"] + ";" + "auth=" + m_currentToken);
            if (content != null) { request.Content = content; }

            // Send out
            HttpResponseMessage responseMessage = client.Send(request);

            // Error stuff
            if (responseMessage.StatusCode != HttpStatusCode.OK) 
            {
                string? responseStr = responseMessage.Content.ReadAsStringAsync().Result;

                Console.WriteLine("Response was not 200...");
                if (responseStr != null) { Console.WriteLine(responseStr); }
                
                throw new Exception();
            }

            return responseMessage;
        }

        public string GetRandomMessage(int forceIndex = -1)
        {
            // Select random
            int index = m_rand.Next(m_possibleMessages.Length - 1);
            if (forceIndex > -1) { index = forceIndex; } //Debug
            string message = m_possibleMessages[index];

            // Format special characters.
            message = message.Replace("{hour}", string.Format("{0:h tt}", DateTime.Now));
            message = message.Trim();
            
            // Return
            return message;
        }

        private static string? GetFoxImage(string userAgentStr)
        {
            //Setup URL
            string baseURL = "https://api.tinyfox.dev";
            string foxURL = baseURL + "/img?animal=fox&json";

            //Setup Client
            HttpClient client = new HttpClient();

            //Add headers
            HttpRequestMessage request = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, foxURL);
            if (!request.Headers.TryAddWithoutValidation("User-Agent", userAgentStr)) { return null; }

            //Get Response
            HttpResponseMessage responseMessage = client.Send(request);
            string? responseJson = responseMessage.Content.ReadAsStringAsync().Result;
            if (responseJson == null) { Console.WriteLine("Response was null..."); return null; }

            //Decode Image URL
            string? maybeURL = (string?)JObject.Parse(responseJson)["loc"];
            if (maybeURL == null) { return null; }

            //Check api limit (usually none)
            string? maybeLimit = (string?)JObject.Parse(responseJson)["remaining_api_calls"];
            if (maybeLimit != null) 
            {
                if (maybeLimit != "not_active")
                {
                    Console.WriteLine("remaining_api_calls: " + maybeLimit);
                    if (int.Parse(maybeLimit) < 1) { Console.WriteLine("Logging out, no more api calls.");  return null; }
                }
            }

            //URL
            return baseURL + maybeURL;
        }

        public static byte[] BitmapToPngByteArray(Bitmap img, bool disposeBitmap = true)
        {
            using (var stream = new MemoryStream())
            {
                if (img == null) { return new byte[0]; }
                img.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                byte[] ret = stream.ToArray();
                if (disposeBitmap) { img.Dispose(); }
                stream.Dispose();
                return ret;
            }
        }
    }
}