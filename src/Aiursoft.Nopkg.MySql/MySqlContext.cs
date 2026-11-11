using System.Diagnostics.CodeAnalysis;
using Aiursoft.Nopkg.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aiursoft.Nopkg.MySql;

[ExcludeFromCodeCoverage]

public class MySqlContext(DbContextOptions<MySqlContext> options) : NopkgDbContext(options);
