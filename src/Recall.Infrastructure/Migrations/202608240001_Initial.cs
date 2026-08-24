using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable
namespace Recall.Infrastructure.Migrations;

[Migration("202608240001_Initial")]
[DbContext(typeof(RecallDbContext))]
public sealed class Initial : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
CREATE TABLE Clients (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, ClientType TEXT NOT NULL, PublicIdentifier TEXT NOT NULL, TokenHash TEXT NOT NULL, IsEnabled INTEGER NOT NULL, CreatedAt TEXT NOT NULL, LastSeenAt TEXT NULL);
CREATE UNIQUE INDEX IX_Clients_PublicIdentifier ON Clients(PublicIdentifier);
CREATE TABLE Permissions (Id TEXT NOT NULL PRIMARY KEY, ClientId TEXT NOT NULL, Category TEXT NOT NULL, CanRead INTEGER NOT NULL, CanCreate INTEGER NOT NULL, CanUpdate INTEGER NOT NULL, CanDelete INTEGER NOT NULL, MaximumSensitivity INTEGER NOT NULL);
CREATE UNIQUE INDEX IX_Permissions_ClientId_Category ON Permissions(ClientId, Category);
CREATE TABLE Memories (Id TEXT NOT NULL PRIMARY KEY, Content TEXT NOT NULL, Summary TEXT NULL, Category TEXT NOT NULL, Sensitivity INTEGER NOT NULL, Importance INTEGER NOT NULL, Confidence REAL NOT NULL, SourceClientId TEXT NOT NULL, SourceConversation TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL, LastAccessedAt TEXT NULL, ExpiresAt TEXT NULL, Status INTEGER NOT NULL, ContentHash TEXT NOT NULL, Version INTEGER NOT NULL);
CREATE INDEX IX_Memories_Category_Status_ExpiresAt ON Memories(Category, Status, ExpiresAt);
CREATE TABLE AuditEvents (Id TEXT NOT NULL PRIMARY KEY, ClientId TEXT NOT NULL, MemoryId TEXT NULL, Action INTEGER NOT NULL, Purpose TEXT NULL, WasAllowed INTEGER NOT NULL, Reason TEXT NOT NULL, Timestamp TEXT NOT NULL);
CREATE INDEX IX_AuditEvents_ClientId_Timestamp ON AuditEvents(ClientId, Timestamp);
CREATE VIRTUAL TABLE MemorySearch USING fts5(MemoryId UNINDEXED, Content, Summary, Category, tokenize='unicode61');
CREATE TRIGGER memory_ai AFTER INSERT ON Memories WHEN new.Status = 0 BEGIN INSERT INTO MemorySearch(MemoryId, Content, Summary, Category) VALUES (new.Id, new.Content, coalesce(new.Summary, ''), new.Category); END;
CREATE TRIGGER memory_au AFTER UPDATE ON Memories BEGIN DELETE FROM MemorySearch WHERE MemoryId = old.Id; INSERT INTO MemorySearch(MemoryId, Content, Summary, Category) SELECT new.Id, new.Content, coalesce(new.Summary, ''), new.Category WHERE new.Status = 0; END;
CREATE TRIGGER memory_ad AFTER DELETE ON Memories BEGIN DELETE FROM MemorySearch WHERE MemoryId = old.Id; END;
""");
    }
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE IF EXISTS MemorySearch; DROP TABLE IF EXISTS AuditEvents; DROP TABLE IF EXISTS Permissions; DROP TABLE IF EXISTS Memories; DROP TABLE IF EXISTS Clients;");
}
