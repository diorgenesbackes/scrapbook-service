namespace ScrapBook.Api.Options;

public sealed class StartupConfigurationException(string optionName) : Exception("Configuração inválida.")
{
    public string OptionName { get; } = optionName;
}
