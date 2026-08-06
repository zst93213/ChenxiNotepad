// 全局 using：保证各模型文件中使用 [JsonPropertyName] 等 JSON 序列化特性时无需逐文件引用。
global using System.Text.Json.Serialization;

// 全局 using：WPF 项目的 ImplicitUsings 不一定包含 System.IO 与 System.Windows.Automation，
// 此处统一声明以避免各文件逐个引用。
global using System.IO;
global using System.Windows.Automation;
