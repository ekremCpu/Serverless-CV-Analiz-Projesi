# ☁️ AI Powered Serverless CV Analyzer

Bu proje, iş başvurularını (CV) analiz etmek için geliştirilmiş, **AWS Serverless** mimarisi üzerinde çalışan yapay zeka destekli bir sistemdir.

## 🚀 Proje Hakkında
Kullanıcılar PDF formatındaki CV'lerini sisteme yükler. Sistem, belirlenen kriterlere (örneğin: "2 yıl deneyimli C# geliştirici") göre CV'yi analiz eder, **0-100 arası puanlar**, özet çıkarır ve **teknik mülakat soruları** hazırlar.

## 🛠️ Kullanılan Teknolojiler & Mimari

* **Backend:** .NET 8 (C#) - AWS Lambda
* **AI (Yapay Zeka):** Amazon Bedrock (Anthropic Claude 3 Haiku)
* **Database:** Amazon DynamoDB (NoSQL)
* **Storage:** Amazon S3
* **Frontend:** HTML5 / JS (S3 Static Hosting)

## ⚙️ Nasıl Çalışır? (Workflow)

1.  **Upload:** Kullanıcı web arayüzünden CV yükler (S3).
2.  **Trigger:** Dosya yüklendiğinde AWS Lambda (C#) tetiklenir.
3.  **Analysis:** Lambda, dosya içeriğini okur ve Prompt Engineering ile Amazon Bedrock'a gönderir.
4.  **Save:** AI'dan gelen Puan ve Özet bilgisi DynamoDB'ye kaydedilir.
5.  **Result:** Sonuçlar frontend ekranında anlık olarak listelenir.

---
*Geliştirici: Ekrem*
