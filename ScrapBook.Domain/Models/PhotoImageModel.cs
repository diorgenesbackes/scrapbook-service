namespace ScrapBook.Domain.Models;

public sealed record PhotoImageModel(string MediaType, string Container, int Width, int Height, short? Orientation);
