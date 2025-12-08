using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Amazon;
using Amazon.Lambda.Core;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.S3Events;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Newtonsoft.Json;
using UglyToad.PdfPig;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace CloudProje
{
    public class Function
    {
        private static readonly AmazonBedrockRuntimeClient bedrockClient = new AmazonBedrockRuntimeClient(RegionEndpoint.USEast1);
        private static readonly AmazonDynamoDBClient dynamoClient = new AmazonDynamoDBClient(RegionEndpoint.USEast1);
        private static readonly IAmazonS3 s3Client = new AmazonS3Client(RegionEndpoint.USEast1);
        private const string TABLE_NAME = "CvHistory";

        // --- 1. METOT: API GATEWAY HANDLER (Frontend için) ---
        public async Task<APIGatewayProxyResponse> FunctionHandler(APIGatewayProxyRequest request, ILambdaContext context)
        {
            context.Logger.LogLine("API Ýsteði Geldi.");

            try
            {
                if (string.IsNullOrEmpty(request.Body)) return CevapDon(400, new { error = "Body boþ." });

                dynamic data = JsonConvert.DeserializeObject(request.Body);
                string action = data?.action;

                // SENARYO 1: GEÇMÝÞÝ GETÝR
                if (action == "get_history")
                {
                    return await GetHistoryAsync();
                }

                // SENARYO 2: API ÜZERÝNDEN ANALÝZ
                string fileBase64 = data?.file_data;
                string criteria = data?.criteria;
                string gelenDosyaAdi = data?.file_name != null ? (string)data.file_name : "Bilinmeyen_Dosya.pdf";

                if (string.IsNullOrEmpty(fileBase64)) return CevapDon(400, new { error = "Dosya yok." });

                byte[] fileBytes = Convert.FromBase64String(fileBase64);

                // API'den gelen dosyaya isim olarak "API_Upload.pdf" veriyoruz
                var result = await AnalyzeCvProcess(fileBytes, criteria, gelenDosyaAdi, context);

                return CevapDon(200, new { analysis = result });
            }
            catch (Exception ex)
            {
                context.Logger.LogLine("API Hatasý: " + ex.ToString());
                return CevapDon(500, new { error = ex.Message });
            }
        }

        // --- 2. METOT: S3 EVENT HANDLER (Otomatik Tetikleme Ýçin) ---
        public async Task S3Handler(S3Event evnt, ILambdaContext context)
        {
            var s3Event = evnt.Records?[0].S3;
            if (s3Event == null) return;

            try
            {
                var bucketName = s3Event.Bucket.Name;
                // Dosya adý burada "key" deðiþkeninde
                var key = System.Net.WebUtility.UrlDecode(s3Event.Object.Key.Replace("+", " "));

                context.Logger.LogLine($"S3 Tetiklendi. Dosya: {key}");

                if (key.EndsWith("/"))
                {
                    context.Logger.LogLine("Bu bir klasör, iþlem yapýlmýyor.");
                    return;
                }

                var response = await s3Client.GetObjectAsync(bucketName, key);
                using (var ms = new MemoryStream())
                {
                    await response.ResponseStream.CopyToAsync(ms);
                    byte[] fileBytes = ms.ToArray();

                    string defaultCriteria = "Genel yetenek analizi, deneyim kontrolü ve öðrenci durumu.";

                    // BURADA "key" deðiþkenini parametre olarak gönderiyoruz
                    await AnalyzeCvProcess(fileBytes, defaultCriteria, key, context);
                }
            }
            catch (Exception e)
            {
                context.Logger.LogLine($"S3 Hatasý: {e.Message}");
                throw;
            }
        }


        // --- ORTAK ÝÞ MANTIÐI ---
        // Buraya "string fileName" parametresi ekledik
        private async Task<dynamic> AnalyzeCvProcess(byte[] fileBytes, string criteria, string fileName, ILambdaContext context)
        {
            string cvText = "";
            try
            {
                using (var pdf = PdfDocument.Open(fileBytes))
                {
                    foreach (var page in pdf.GetPages()) cvText += page.Text + " ";
                }
            }
            catch
            {
                throw new Exception("PDF Okunamadý.");
            }

            var prompt = $@"
                ROL: Sen çok katý kurallarý olan bir 'Teknik Denetçi'sin.
                GÖREV: Aþaðýdaki CV verilerini analiz et ve JSON formatýnda raporla.
                KRÝTERLER: {criteria}
                CV ÝÇERÝÐÝ: {cvText}
                
                KURALLAR:
                1. Öðrenci Kontrolü: Tarihler 'Devam Ediyor' ise veya gelecek tarihteyse deneyim 0 sayýlýr.
                2. Stajlar deneyim sayýlmaz.
                3. Puanlama: Kriter karþýlanmýyorsa 40 altý ver.

                ÇIKTI FORMATI (SADECE JSON):
                {{
                  ""uyumluluk_puani"": (0-100 arasý sayý),
                  ""ozet"": ""(Türkçe özet)"",
                  ""mulakat_sorulari"": [""Soru 1"", ""Soru 2"", ""Soru 3""]
                }}";

            var claudePayload = new
            {
                anthropic_version = "bedrock-2023-05-31",
                max_tokens = 1000,
                messages = new[] { new { role = "user", content = new[] { new { type = "text", text = prompt } } } }
            };

            var invokeResponse = await bedrockClient.InvokeModelAsync(new InvokeModelRequest
            {
                ModelId = "anthropic.claude-3-haiku-20240307-v1:0",
                Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(claudePayload))),
                ContentType = "application/json",
                Accept = "application/json"
            });

            string aiResultRaw;
            using (var reader = new StreamReader(invokeResponse.Body))
            {
                dynamic json = JsonConvert.DeserializeObject(await reader.ReadToEndAsync());
                aiResultRaw = json.content[0].text;
            }

            string cleanJson = CleanAIResponse(aiResultRaw);
            dynamic parsedResult = JsonConvert.DeserializeObject(cleanJson);

            string ozet = parsedResult.ozet;
            string puan = parsedResult.uyumluluk_puani.ToString();

            // DYNAMODB KAYIT
            var dbItem = new Dictionary<string, AttributeValue>
            {
                { "Id", new AttributeValue { S = Guid.NewGuid().ToString() } },
                { "DosyaAdi", new AttributeValue { S = fileName } }, // ARTIK DOSYA ADINI BURAYA YAZIYORUZ
                { "Tarih", new AttributeValue { S = DateTime.Now.ToString("yyyy-MM-dd HH:mm") } },
                { "AnalizOzeti", new AttributeValue { S = ozet.Length > 150 ? ozet.Substring(0, 150) + "..." : ozet } },
                { "TamAnaliz", new AttributeValue { S = cleanJson } },
                { "Puan", new AttributeValue { S = puan } }
            };

            await dynamoClient.PutItemAsync(new PutItemRequest { TableName = TABLE_NAME, Item = dbItem });

            return parsedResult;
        }

        // --- YARDIMCI METOTLAR ---
        private async Task<APIGatewayProxyResponse> GetHistoryAsync()
        {
            var scanRequest = new ScanRequest { TableName = TABLE_NAME, Limit = 20 };
            var response = await dynamoClient.ScanAsync(scanRequest);
            var historyList = new List<dynamic>();
            foreach (var item in response.Items)
            {
                historyList.Add(new
                {
                    // History listesinde de DosyaAdi'ni gösterelim
                    Dosya = item.ContainsKey("DosyaAdi") ? item["DosyaAdi"].S : "Bilinmiyor",
                    Tarih = item.ContainsKey("Tarih") ? item["Tarih"].S : "-",
                    Ozet = item.ContainsKey("AnalizOzeti") ? item["AnalizOzeti"].S : "-",
                    Puan = item.ContainsKey("Puan") ? item["Puan"].S : "0"
                });
            }
            return CevapDon(200, new { history = historyList });
        }

        private string CleanAIResponse(string response)
        {
            response = response.Replace("```json", "").Replace("```", "").Trim();
            int firstBrace = response.IndexOf("{");
            int lastBrace = response.LastIndexOf("}");
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return response.Substring(firstBrace, lastBrace - firstBrace + 1);
            }
            return response;
        }

        private APIGatewayProxyResponse CevapDon(int kod, object veri)
        {
            return new APIGatewayProxyResponse
            {
                StatusCode = kod,
                Body = JsonConvert.SerializeObject(veri),
                Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
            };
        }
    }
}