using ScrapBook.Domain.Entities;
using System.Security.Cryptography;

namespace ScrapBook.Domain.Models;

public sealed record EventChangeModel(EventEntity State, bool Changed, bool AccessChanged);
