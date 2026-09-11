# MailAPI 文檔

# Mail API 使用說明

> 服務網址：`https://scm.ecs.com.tw:8020`
> 
> 
> Swagger UI：`https://scm.ecs.com.tw:8020/docs`
> 

---

## 目錄

- [API 總覽](about:blank#api-%E7%B8%BD%E8%A6%BD)
- [發送郵件](about:blank#%E7%99%BC%E9%80%81%E9%83%B5%E4%BB%B6-post-apisend)
    - [請求欄位說明](about:blank#%E8%AB%8B%E6%B1%82%E6%AC%84%E4%BD%8D%E8%AA%AA%E6%98%8E)
    - [回應格式](about:blank#%E5%9B%9E%E6%87%89%E6%A0%BC%E5%BC%8F)
    - [範例：純文字信件](about:blank#%E7%AF%84%E4%BE%8B%E7%B4%94%E6%96%87%E5%AD%97%E4%BF%A1%E4%BB%B6)
    - [範例：多收件人 + 副本](about:blank#%E7%AF%84%E4%BE%8B%E5%A4%9A%E6%94%B6%E4%BB%B6%E4%BA%BA--%E5%89%AF%E6%9C%AC)
    - [範例：附件](about:blank#%E7%AF%84%E4%BE%8B%E9%99%84%E4%BB%B6)
    - [範例：內嵌圖片](about:blank#%E7%AF%84%E4%BE%8B%E5%85%A7%E5%B5%8C%E5%9C%96%E7%89%87)
    - [範例：base64 附件/內嵌圖片](about:blank#%E7%AF%84%E4%BE%8Bbase64-%E9%99%84%E4%BB%B6%E5%85%A7%E5%B5%8C%E5%9C%96%E7%89%87)
- [.NET Core MVC 整合範例](about:blank#net-core-mvc-%E6%95%B4%E5%90%88%E7%AF%84%E4%BE%8B)
- [curl 測試指令](about:blank#curl-%E6%B8%AC%E8%A9%A6%E6%8C%87%E4%BB%A4)
- [錯誤排查](about:blank#%E9%8C%AF%E8%AA%A4%E6%8E%92%E6%9F%A5)
- [已知問題紀錄](about:blank#%E5%B7%B2%E7%9F%A5%E5%95%8F%E9%A1%8C%E7%B4%80%E9%8C%84)

---

## API 總覽

| 方法 | 路徑 | 說明 |
| --- | --- | --- |
| GET | `/health` | 健康檢查，確認服務存活 |
| POST | `/api/send` | 發送郵件（含附件、內嵌圖片） |

---

## 發送郵件 `POST /api/send`

### 請求欄位說明

Content-Type：`application/json`

| 欄位 | 型別 | 必填 | 說明 |
| --- | --- | --- | --- |
| `username` | string | ✅ | AD 帳號（不含 domain，例如 `jackychuang`、`WendyYen`）；格式因人而異，**不一定等於信箱前綴** |
| `password` | string | ✅ | AD 密碼 |
| `to` | string \| string[] | ✅ | 收件人信箱，可單一字串或陣列 |
| `subject` | string |  | 郵件主旨（預設：`測試信件`） |
| `body` | string |  | HTML 格式內文 |
| `cc` | string \| string[] |  | 副本收件人，可單一字串或陣列 |
| `from_email` | string |  | 明確指定寄件信箱（選填）。**AD 帳號格式與信箱格式不同時必須填寫**，例如帳號 `WendyYen` 對應信箱 `Wendy.Yen@ecs.com.tw`。留空時自動使用 `{username}@ecs.com.tw` |
| `attachments` | object[] |  | 一般附件，見下方說明 |
| `inlineImages` | object[] |  | 內嵌圖片，見下方說明 |

### `attachments` 物件格式

```json
{
  "filename": "report.pdf",
  "url": "https://your-server/files/report.pdf"
}
```

或直接帶檔案內容（不透過 URL 下載）：

```json
{
  "filename": "report.pdf",
  "content_base64": "JVBERi0xLjQKJ..."
}
```

- `filename`：**必須明確提供**，顯示於信件的附件檔名
- `url`：伺服器可存取的檔案下載網址
- `content_base64`：檔案內容的 base64 編碼字串，適合呼叫端動態產生檔案（如報表）、不想額外提供下載網址的情境
- `url` 與 `content_base64` **擇一提供即可**，兩者都不填會回傳 422 錯誤；若兩者都填，優先使用 `content_base64`

### `inlineImages` 物件格式

```json
{
  "cid": "logo",
  "url": "https://your-server/images/logo.png"
}
```

或直接帶圖片內容：

```json
{
  "cid": "logo",
  "content_base64": "iVBORw0KGgoAAAANSUhEUgAA..."
}
```

- `cid`：Content ID，在 `body` 中以 `<img src="cid:logo">` 引用
- `url`：伺服器可存取的圖片下載網址
- `content_base64`：圖片內容的 base64 編碼字串
- `url` 與 `content_base64` **擇一提供即可**，兩者都不填會回傳 422 錯誤；若兩者都填，優先使用 `content_base64`

---

### 回應格式

```json
{
  "success": true,
  "message": "郵件發送成功 | 寄件人: jacky.chuang@ecs.com.tw → 收件人: someone@ecs.com.tw"
}
```

| HTTP 狀態碼 | 說明 |
| --- | --- |
| `200` | 發送成功 |
| `400` | 缺少必填欄位（username/password/to） |
| `500` | 發送失敗（帳密錯誤、EWS 連線異常等） |

---

### 範例：純文字信件

```json
{
  "username": "jackychuang",
  "password": "your_password",
  "to": "someone@ecs.com.tw",
  "subject": "Hello",
  "body": "<p>這是一封測試信。</p>"
}
```

---

### 範例：AD 帳號與信箱格式不同（需指定 from_email）

當 AD 帳號（如 `WendyYen`）與信箱前綴（如 `Wendy.Yen`）格式不同時，必須明確傳入 `from_email`，否則 Exchange 會回傳「沒有與 SMTP 位址關聯的信箱」錯誤。

```json
{
  "username": "WendyYen",
  "password": "your_password",
  "from_email": "Wendy.Yen@ecs.com.tw",
  "to": "someone@ecs.com.tw",
  "subject": "Hello",
  "body": "<p>這是一封測試信。</p>"
}
```

---

### 範例：多收件人 + 副本

```json
{
  "username": "jackychuang",
  "password": "your_password",
  "to": ["alice@ecs.com.tw", "bob@ecs.com.tw"],
  "cc": "manager@ecs.com.tw",
  "subject": "專案進度通知",
  "body": "<h2>本週進度</h2><p>請參閱附件。</p>"
}
```

---

### 範例：附件

```json
{
  "username": "jackychuang",
  "password": "your_password",
  "to": "someone@ecs.com.tw",
  "subject": "附上報表",
  "body": "<p>請見附件。</p>",
  "attachments": [
    {
      "filename": "report_2026Q2.pdf",
      "url": "https://scm.ecs.com.tw/files/report.pdf"
    }
  ]
}
```

---

### 範例：內嵌圖片

```json
{
  "username": "jackychuang",
  "password": "your_password",
  "to": "someone@ecs.com.tw",
  "subject": "電子報",
  "body": "<h1>公告</h1><img src='cid:banner' style='width:600px'>",
  "inlineImages": [
    {
      "cid": "banner",
      "url": "https://scm.ecs.com.tw/images/banner.png"
    }
  ]
}
```

---

### 範例：base64 附件/內嵌圖片

適合檔案內容是動態產生（例如報表、PDF），沒有對外可存取 URL 的情境。

```json
{
  "username": "jackychuang",
  "password": "your_password",
  "to": "someone@ecs.com.tw",
  "subject": "動態報表",
  "body": "<p>請見附件</p><img src='cid:chart1'>",
  "attachments": [
    {
      "filename": "report.pdf",
      "content_base64": "JVBERi0xLjQKJ..."
    }
  ],
  "inlineImages": [
    {
      "cid": "chart1",
      "content_base64": "iVBORw0KGgoAAAANSUhEUgAA..."
    }
  ]
}
```

> ⚠️ base64 編碼後檔案大小會膨脹約 33%，大附件請留意 request body 大小限制。

---

## .NET Core MVC 整合範例

### 1. 定義 DTO 類別

```csharp
// Models/MailApiRequest.cs
public class MailApiRequest
{
    public string Username { get; set; }
    public string Password { get; set; }
    public List<string> To { get; set; }
    public string Subject { get; set; }
    public string Body { get; set; }
    public List<string> Cc { get; set; } = new();
    /// <summary>AD 帳號與信箱格式不同時填寫，例如帳號 WendyYen 對應信箱 Wendy.Yen@ecs.com.tw</summary>
    public string FromEmail { get; set; } = "";
    public List<AttachmentItem> Attachments { get; set; } = new();
    public List<InlineImageItem> InlineImages { get; set; } = new();
}

public class AttachmentItem
{
    public string Filename { get; set; }
    /// <summary>與 ContentBase64 擇一提供</summary>
    public string Url { get; set; }
    /// <summary>檔案內容 base64 編碼，與 Url 擇一提供</summary>
    public string ContentBase64 { get; set; }
}

public class InlineImageItem
{
    public string Cid { get; set; }
    /// <summary>與 ContentBase64 擇一提供</summary>
    public string Url { get; set; }
    /// <summary>圖片內容 base64 編碼，與 Url 擇一提供</summary>
    public string ContentBase64 { get; set; }
}

public class MailApiResponse
{
    public bool Success { get; set; }
    public string Message { get; set; }
}
```

### 2. 註冊 HttpClient（Program.cs）

```csharp
// Program.cs
builder.Services.AddHttpClient("MailApi", client =>
{
    client.BaseAddress = new Uri("https://scm.ecs.com.tw:8020");
    client.Timeout = TimeSpan.FromSeconds(60);
});
```

### 3. 發送郵件的 Service

```csharp
// Services/MailService.cs
using System.Net.Http.Json;

public class MailService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public MailService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<MailApiResponse> SendAsync(MailApiRequest request)
    {
        var client = _httpClientFactory.CreateClient("MailApi");
        var response = await client.PostAsJsonAsync("/api/send", request);
        var result = await response.Content.ReadFromJsonAsync<MailApiResponse>();
        return result!;
    }
}
```

### 4. 在 Controller 使用

```csharp
// Controllers/NotificationController.cs
public class NotificationController : Controller
{
    private readonly MailService _mailService;

    public NotificationController(MailService mailService)
    {
        _mailService = mailService;
    }

    [HttpPost]
    public async Task<IActionResult> SendNotification()
    {
        var request = new MailApiRequest
        {
            Username = "jackychuang",
            Password = "your_password",
            // AD 帳號與信箱格式不同時加上 FromEmail
            // FromEmail = "Wendy.Yen@ecs.com.tw",
            To = new List<string> { "someone@ecs.com.tw" },
            Subject = "系統通知",
            Body = "<p>這是一封系統自動發出的通知信。</p>",
        };

        var result = await _mailService.SendAsync(request);

        if (result.Success)
            return Ok(result.Message);
        else
            return StatusCode(500, result.Message);
    }
}
```

### 5. appsettings.json 管理帳密（建議做法）

```json
{
  "MailApi": {
    "Username": "jackychuang",
    "Password": "your_password",
    "FromEmail": ""
  }
}
```

```csharp
// 注入 IConfiguration 後使用
var username = _configuration["MailApi:Username"];
var password = _configuration["MailApi:Password"];
```

---

## curl 測試指令

### 健康檢查

```bash
curl https://scm.ecs.com.tw:8020/health
```

### 發送基本信件（單行）

```bash
curl.exe -k -X POST "https://scm.ecs.com.tw:8020/api/send" -H "Content-Type: application/json" -d "{""username"":""jackychuang"",""password"":""your_password"",""to"":""someone@ecs.com.tw"",""subject"":""Test"",""body"":""<p>Hello from curl</p>""}"
```

### 發送基本信件（AD 帳號與信箱格式不同，需指定 from_email）

```bash
curl.exe -k -X POST "https://scm.ecs.com.tw:8020/api/send" -H "Content-Type: application/json" -d "{""username"":""WendyYen"",""password"":""your_password"",""from_email"":""Wendy.Yen@ecs.com.tw"",""to"":""someone@ecs.com.tw"",""subject"":""Test"",""body"":""<p>Hello from curl</p>""}"
```

### 發送含附件信件（單行）

```bash
curl.exe -k -X POST "https://scm.ecs.com.tw:8020/api/send" -H "Content-Type: application/json" -d "{""username"":""jackychuang"",""password"":""your_password"",""to"":""someone@ecs.com.tw"",""subject"":""附件測試"",""body"":""<p>請見附件</p>"",""attachments"":[{""filename"":""test.pdf"",""url"":""https://your-server/test.pdf""}]}"
```

---

## 錯誤排查

| 現象 | 原因 | 解法 |
| --- | --- | --- |
| `success: false`，訊息含 `Authentication` | 帳號或密碼錯誤 | 確認 AD 帳號密碼（不含 domain） |
| `success: false`，訊息含 `沒有與 SMTP 位址關聯的信箱` | AD 帳號格式與信箱前綴不同，例如帳號 `WendyYen` 但信箱是 `Wendy.Yen@ecs.com.tw` | 加上 `from_email` 欄位明確指定完整信箱位址 |
| `success: false`，訊息含 `timeout` | 附件/圖片 URL 無法從伺服器存取 | 確認 URL 為伺服器可達的位址 |
| HTTP 400 | 缺少 `username`、`password` 或 `to` | 補齊必填欄位 |
| HTTP 422 | `attachments`/`inlineImages` 某筆項目 `url` 與 `content_base64` 都未提供 | 兩者擇一填寫 |
| HTTP 500 | EWS 連線失敗 | 確認 EWS 服務正常 |
| 連線逾時 | 服務未啟動 | 先 GET `/health` 確認服務存活 |

---

## 已知問題紀錄

### AD 帳號與信箱格式不一致（2026-06-26）

**問題：** 部分帳號（如 `WendyYen`）使用預設行為（`username@ecs.com.tw`）發信時，Exchange 回傳「沒有與 SMTP 位址關聯的信箱」。

**根本原因：** Exchange 的 AD 登入帳號（sAMAccountName）與信箱的 primary SMTP 位址格式不一定相同。原程式直接以 `{username}@ecs.com.tw` 作為信箱位址，在格式不一致時會找不到信箱。部分帳號（如 `jackychuang`）能正常使用，是因為 Exchange 管理員剛好為其設定了同名的信箱別名（Proxy Address），屬於偶然相容，並非程式正確。

**修正：** v1.2.0 新增 `from_email` 選填欄位，讓呼叫方可明確指定寄件信箱，留空時維持原行為以確保向下相容。

### 新增 `content_base64` 欄位（2026-07-02）

**需求：** 部分呼叫端的附件/內嵌圖片內容是動態產生（如報表），沒有對外可存取的下載 URL，原本只支援 `url` 下載方式無法滿足。

**修正：** `attachments`/`inlineImages` 每筆項目新增選填欄位 `content_base64`，與 `url` 擇一提供；兩者皆空時回傳 422 驗證錯誤。原本只帶 `url` 的呼叫端不受影響。