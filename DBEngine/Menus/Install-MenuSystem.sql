-- =============================================================================================
-- MenuSystem - install from scratch
-- =============================================================================================
-- Creates the schema, tables and procedures behind MDDFoundation.Menus / MDDWinForms.Menus.
-- Application independent: it seeds nothing and imports nothing. Register an application and
-- build its menu through the launcher, or with MenuSystem.MenuItem_Save.
--
-- Run against the database that is to hold the menu definitions. It stops if MenuSystem is
-- already present rather than half-applying itself; to change an existing installation, use a
-- transform script against that database instead.
--
-- After registering an application, every user of it needs SELECT on the MenuSystem tables and
-- EXECUTE on the MenuSystem procedures.
-- =============================================================================================
SET XACT_ABORT ON;
GO
IF SCHEMA_ID(N'MenuSystem') IS NOT NULL
BEGIN
    RAISERROR(N'MenuSystem already exists in this database. This script installs from scratch.', 16, 1);
    SET NOEXEC ON;
END;
GO
CREATE SCHEMA [MenuSystem] AUTHORIZATION [dbo];
GO
-- ------------------------------------------------------------------ Tables and triggers
GO
CREATE TABLE [MenuSystem].[Application] (
    [ApplicationKey]     NVARCHAR (100) NOT NULL,
    [Title]              NVARCHAR (200) NOT NULL,
    [created_date]       DATETIME       CONSTRAINT [DF_MenuSystem_Application_created_date] DEFAULT (getdate()) NOT NULL,
    [modified_date]      DATETIME       NULL,
    [SimpleMode]         BIT            CONSTRAINT [DF_MenuSystem_Application_SimpleMode] DEFAULT ((1)) NOT NULL,
    [UsageRetentionDays] INT            CONSTRAINT [DF_MenuSystem_Application_UsageRetentionDays] DEFAULT ((90)) NULL,
    CONSTRAINT [PK_MenuSystem_Application] PRIMARY KEY CLUSTERED ([ApplicationKey] ASC),
    CONSTRAINT [CK_Application_UsageRetentionDays] CHECK ([UsageRetentionDays] IS NULL OR [UsageRetentionDays]>=(1) AND [UsageRetentionDays]<=(36500))
);




GO
CREATE OR ALTER TRIGGER MenuSystem.trgApplication_modified_date
    ON MenuSystem.Application AFTER UPDATE AS
    BEGIN
        SET NOCOUNT ON;
        IF TRIGGER_NESTLEVEL(
            OBJECT_ID(N'MenuSystem.trgApplication_modified_date')) > 1 RETURN;
        UPDATE tgt SET modified_date = GETDATE()
        FROM MenuSystem.Application tgt
        JOIN inserted i ON tgt.ApplicationKey = i.ApplicationKey;
    END;
GO
CREATE TABLE [MenuSystem].[User] (
    [Id]            INT            IDENTITY (1, 1) NOT NULL,
    [IdentityKey]   NVARCHAR (256) NOT NULL,
    [DisplayName]   NVARCHAR (256) NOT NULL,
    [created_date]  DATETIME       CONSTRAINT [DF_MenuSystem_User_created_date] DEFAULT (getdate()) NOT NULL,
    [modified_date] DATETIME       NULL,
    CONSTRAINT [PK_MenuSystem_User] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [UQ_MenuSystem_User_IdentityKey] UNIQUE NONCLUSTERED ([IdentityKey] ASC)
);


GO
CREATE OR ALTER TRIGGER MenuSystem.trgUser_modified_date
ON MenuSystem.[User] AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL(OBJECT_ID(N'MenuSystem.trgUser_modified_date')) > 1 RETURN;
    UPDATE tgt SET modified_date = GETDATE() FROM MenuSystem.[User] tgt JOIN inserted i ON tgt.Id = i.Id;
END;
GO
CREATE TABLE [MenuSystem].[MenuItem] (
    [Id]                INT             IDENTITY (1, 1) NOT NULL,
    [ApplicationKey]    NVARCHAR (100)  NOT NULL,
    [ParentId]          INT             NULL,
    [Kind]              INT             CONSTRAINT [DF_MenuSystem_Kind] DEFAULT ((1)) NOT NULL,
    [Title]             NVARCHAR (200)  NOT NULL,
    [Description]       NVARCHAR (1000) NULL,
    [IconKey]           NVARCHAR (200)  NULL,
    [Keywords]          NVARCHAR (1000) NULL,
    [SortOrder]         INT             CONSTRAINT [DF_MenuSystem_SortOrder] DEFAULT ((0)) NOT NULL,
    [TargetKind]        INT             CONSTRAINT [DF_MenuSystem_TargetKind] DEFAULT ((0)) NOT NULL,
    [TargetTypeName]    NVARCHAR (1000) NULL,
    [AssemblyName]      NVARCHAR (300)  NULL,
    [ExecutablePath]    NVARCHAR (2000) NULL,
    [Arguments]         NVARCHAR (2000) NULL,
    [DefaultLaunchMode] INT             CONSTRAINT [DF_MenuSystem_LaunchMode] DEFAULT ((0)) NOT NULL,
    [AllowNewInstance]  BIT             CONSTRAINT [DF_MenuSystem_AllowNew] DEFAULT ((0)) NOT NULL,
    [created_date]      DATETIME        CONSTRAINT [DF_MenuSystem_MenuItem_created_date] DEFAULT (getdate()) NOT NULL,
    [modified_date]     DATETIME        NULL,
    [ProcedureName]     NVARCHAR (517)  NULL,
    [Retired]           BIT             CONSTRAINT [DF_MenuSystem_MenuItem_Retired] DEFAULT ((0)) NOT NULL,
    CONSTRAINT [PK_MenuSystem_MenuItem] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [CK_MenuSystem_ActionPlacement] CHECK ([Kind]=(0) OR [ParentId] IS NULL),
    CONSTRAINT [CK_MenuSystem_Kind] CHECK ([Kind]=(1) OR [Kind]=(0)),
    CONSTRAINT [CK_MenuSystem_LaunchMode] CHECK ([DefaultLaunchMode]=(1) OR [DefaultLaunchMode]=(0)),
    CONSTRAINT [CK_MenuSystem_Parent] CHECK ([ParentId] IS NULL OR [ParentId]<>[Id]),
    CONSTRAINT [CK_MenuSystem_Target] CHECK ([Kind]=(0) AND [TargetTypeName] IS NULL AND [ExecutablePath] IS NULL AND [ProcedureName] IS NULL OR [Kind]=(1) AND [TargetKind]=(0) AND [TargetTypeName] IS NOT NULL AND len(ltrim(rtrim([TargetTypeName])))>(0) AND [ExecutablePath] IS NULL AND [ProcedureName] IS NULL OR [Kind]=(1) AND [TargetKind]=(1) AND [ExecutablePath] IS NOT NULL AND len(ltrim(rtrim([ExecutablePath])))>(0) AND [TargetTypeName] IS NULL AND [ProcedureName] IS NULL AND [DefaultLaunchMode]=(1) OR [Kind]=(1) AND [TargetKind]=(2) AND [ProcedureName] IS NOT NULL AND len(ltrim(rtrim([ProcedureName])))>(0) AND [TargetTypeName] IS NULL AND [ExecutablePath] IS NULL),
    CONSTRAINT [CK_MenuSystem_TargetKind] CHECK ([TargetKind]=(2) OR [TargetKind]=(1) OR [TargetKind]=(0)),
    CONSTRAINT [CK_MenuSystem_Title] CHECK (len(ltrim(rtrim([Title])))>(0)),
    CONSTRAINT [FK_MenuSystem_Application] FOREIGN KEY ([ApplicationKey]) REFERENCES [MenuSystem].[Application] ([ApplicationKey]),
    CONSTRAINT [FK_MenuSystem_Parent] FOREIGN KEY ([ApplicationKey], [ParentId]) REFERENCES [MenuSystem].[MenuItem] ([ApplicationKey], [Id]),
    CONSTRAINT [UQ_MenuSystem_ApplicationItem] UNIQUE NONCLUSTERED ([ApplicationKey] ASC, [Id] ASC)
);






GO
CREATE OR ALTER TRIGGER MenuSystem.trgMenuItem_modified_date
    ON MenuSystem.MenuItem AFTER UPDATE AS
    BEGIN
        SET NOCOUNT ON;
        IF TRIGGER_NESTLEVEL(
            OBJECT_ID(N'MenuSystem.trgMenuItem_modified_date')) > 1 RETURN;
        UPDATE tgt SET modified_date = GETDATE()
        FROM MenuSystem.MenuItem tgt
        JOIN inserted i ON tgt.Id = i.Id;
    END;
GO
CREATE TABLE [MenuSystem].[MenuItemCategory] (
    [ApplicationKey] NVARCHAR (100) NOT NULL,
    [MenuItemId]     INT            NOT NULL,
    [CategoryId]     INT            NOT NULL,
    [SortOrder]      INT            CONSTRAINT [DF_MenuSystem_MenuItemCategory_SortOrder] DEFAULT ((0)) NOT NULL,
    [created_date]   DATETIME       CONSTRAINT [DF_MenuSystem_MenuItemCategory_created_date] DEFAULT (getdate()) NOT NULL,
    [modified_date]  DATETIME       NULL,
    CONSTRAINT [PK_MenuSystem_MenuItemCategory] PRIMARY KEY CLUSTERED ([MenuItemId] ASC, [CategoryId] ASC),
    CONSTRAINT [CK_MenuSystem_MenuItemCategory_Self] CHECK ([MenuItemId]<>[CategoryId]),
    CONSTRAINT [FK_MenuSystem_MenuItemCategory_Category] FOREIGN KEY ([ApplicationKey], [CategoryId]) REFERENCES [MenuSystem].[MenuItem] ([ApplicationKey], [Id]),
    CONSTRAINT [FK_MenuSystem_MenuItemCategory_Item] FOREIGN KEY ([ApplicationKey], [MenuItemId]) REFERENCES [MenuSystem].[MenuItem] ([ApplicationKey], [Id])
);


GO
CREATE NONCLUSTERED INDEX [IX_MenuSystem_MenuItemCategory_Browse]
    ON [MenuSystem].[MenuItemCategory]([ApplicationKey] ASC, [CategoryId] ASC, [SortOrder] ASC, [MenuItemId] ASC);


GO
-- "At least one category" cannot be a CHECK constraint: the MenuItem row necessarily exists before
-- its first membership row. Enforce the realistic failure instead — removing the last one — and
-- keep the kinds honest on the way in. The client validator refuses to render an orphaned action.
CREATE OR ALTER TRIGGER MenuSystem.trgMenuItemCategory_Validate
ON MenuSystem.MenuItemCategory AFTER INSERT, UPDATE, DELETE AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS(SELECT 1 FROM inserted i
        JOIN MenuSystem.MenuItem m ON m.ApplicationKey = i.ApplicationKey AND m.Id = i.MenuItemId
        WHERE m.Kind <> 1)
        THROW 50000, 'Only launchable menu items can be placed in a category.', 1;
    IF EXISTS(SELECT 1 FROM inserted i
        JOIN MenuSystem.MenuItem c ON c.ApplicationKey = i.ApplicationKey AND c.Id = i.CategoryId
        WHERE c.Kind <> 0)
        THROW 50000, 'Menu items can only be placed in categories.', 1;
    IF EXISTS(SELECT 1 FROM deleted d
        JOIN MenuSystem.MenuItem m ON m.ApplicationKey = d.ApplicationKey AND m.Id = d.MenuItemId
        WHERE m.Kind = 1 AND NOT EXISTS(SELECT 1 FROM MenuSystem.MenuItemCategory remaining
            WHERE remaining.ApplicationKey = m.ApplicationKey AND remaining.MenuItemId = m.Id))
        THROW 50000, 'Every launchable menu item must remain in at least one category.', 1;
END;
GO
CREATE OR ALTER TRIGGER MenuSystem.trgMenuItemCategory_modified_date
ON MenuSystem.MenuItemCategory AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL(OBJECT_ID(N'MenuSystem.trgMenuItemCategory_modified_date')) > 1 RETURN;
    UPDATE tgt SET modified_date = GETDATE()
    FROM MenuSystem.MenuItemCategory tgt
    JOIN inserted i ON tgt.MenuItemId = i.MenuItemId AND tgt.CategoryId = i.CategoryId;
END;
GO
CREATE TABLE [MenuSystem].[UserFavourites] (
    [UserId]         INT            NOT NULL,
    [ApplicationKey] NVARCHAR (100) NOT NULL,
    [MenuItemId]     INT            NOT NULL,
    [SortOrder]      INT            NOT NULL,
    [created_date]   DATETIME       CONSTRAINT [DF_MenuSystem_UserFavourites_created_date] DEFAULT (getdate()) NOT NULL,
    [modified_date]  DATETIME       NULL,
    CONSTRAINT [PK_MenuSystem_UserFavourites] PRIMARY KEY CLUSTERED ([UserId] ASC, [MenuItemId] ASC),
    CONSTRAINT [CK_MenuSystem_UserFavourites_SortOrder] CHECK ([SortOrder]>(0)),
    CONSTRAINT [FK_MenuSystem_UserFavourites_MenuItem] FOREIGN KEY ([ApplicationKey], [MenuItemId]) REFERENCES [MenuSystem].[MenuItem] ([ApplicationKey], [Id]),
    CONSTRAINT [FK_MenuSystem_UserFavourites_User] FOREIGN KEY ([UserId]) REFERENCES [MenuSystem].[User] ([Id])
);


GO
CREATE OR ALTER TRIGGER MenuSystem.trgUserFavourites_modified_date
ON MenuSystem.UserFavourites AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL(OBJECT_ID(N'MenuSystem.trgUserFavourites_modified_date')) > 1 RETURN;
    UPDATE tgt SET modified_date = GETDATE() FROM MenuSystem.UserFavourites tgt
    JOIN inserted i ON tgt.UserId = i.UserId AND tgt.MenuItemId = i.MenuItemId;
END;
GO
CREATE TABLE [MenuSystem].[UserSetting] (
    [UserId]         INT            NOT NULL,
    [ApplicationKey] NVARCHAR (100) NOT NULL,
    [Name]           NVARCHAR (64)  NOT NULL,
    [Value]          NVARCHAR (400) NULL,
    [created_date]   DATETIME       CONSTRAINT [DF_MenuSystem_UserSetting_created_date] DEFAULT (getdate()) NOT NULL,
    [modified_date]  DATETIME       NULL,
    CONSTRAINT [PK_MenuSystem_UserSetting] PRIMARY KEY CLUSTERED ([UserId] ASC, [ApplicationKey] ASC, [Name] ASC),
    CONSTRAINT [CK_MenuSystem_UserSetting_Name] CHECK (len(ltrim(rtrim([Name])))>(0)),
    CONSTRAINT [FK_MenuSystem_UserSetting_Application] FOREIGN KEY ([ApplicationKey]) REFERENCES [MenuSystem].[Application] ([ApplicationKey]),
    CONSTRAINT [FK_MenuSystem_UserSetting_User] FOREIGN KEY ([UserId]) REFERENCES [MenuSystem].[User] ([Id])
);


GO
CREATE OR ALTER TRIGGER MenuSystem.trgUserSetting_modified_date
ON MenuSystem.UserSetting AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL(OBJECT_ID(N'MenuSystem.trgUserSetting_modified_date')) > 1 RETURN;
    UPDATE tgt SET modified_date = GETDATE()
    FROM MenuSystem.UserSetting tgt
    JOIN inserted i ON tgt.UserId = i.UserId AND tgt.ApplicationKey = i.ApplicationKey AND tgt.Name = i.Name;
END;
GO
CREATE TABLE [MenuSystem].[UsageLog] (
    [Id]             BIGINT           IDENTITY (1, 1) NOT NULL,
    [UserId]         INT              NOT NULL,
    [ApplicationKey] NVARCHAR (100)   NOT NULL,
    [MenuItemId]     INT              NOT NULL,
    [LaunchMode]     INT              NOT NULL,
    [SessionId]      UNIQUEIDENTIFIER NOT NULL,
    [MachineName]    NVARCHAR (128)   NOT NULL,
    [created_date]   DATETIME         CONSTRAINT [DF_MenuSystem_UsageLog_created_date] DEFAULT (getdate()) NOT NULL,
    [modified_date]  DATETIME         NULL,
    CONSTRAINT [PK_MenuSystem_UsageLog] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [CK_MenuSystem_UsageLog_LaunchMode] CHECK ([LaunchMode]=(1) OR [LaunchMode]=(0)),
    CONSTRAINT [FK_MenuSystem_UsageLog_MenuItem] FOREIGN KEY ([ApplicationKey], [MenuItemId]) REFERENCES [MenuSystem].[MenuItem] ([ApplicationKey], [Id]),
    CONSTRAINT [FK_MenuSystem_UsageLog_User] FOREIGN KEY ([UserId]) REFERENCES [MenuSystem].[User] ([Id])
);


GO
CREATE NONCLUSTERED INDEX [IX_MenuSystem_UsageLog_Recent]
    ON [MenuSystem].[UsageLog]([UserId] ASC, [ApplicationKey] ASC, [MenuItemId] ASC, [created_date] DESC);


GO
CREATE NONCLUSTERED INDEX [IX_MenuSystem_UsageLog_Retention]
    ON [MenuSystem].[UsageLog]([ApplicationKey] ASC, [created_date] ASC, [Id] ASC);


GO
CREATE OR ALTER TRIGGER MenuSystem.trgUsageLog_modified_date
ON MenuSystem.UsageLog AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL(OBJECT_ID(N'MenuSystem.trgUsageLog_modified_date')) > 1 RETURN;
    UPDATE tgt SET modified_date = GETDATE() FROM MenuSystem.UsageLog tgt JOIN inserted i ON tgt.Id = i.Id;
END;
GO
CREATE TABLE [MenuSystem].[QuickRunParameter] (
    [Id]            INT            IDENTITY (1, 1) NOT NULL,
    [ProcedureName] NVARCHAR (517) NOT NULL,
    [ParameterName] NVARCHAR (128) NOT NULL,
    [DefaultValue]  NVARCHAR (MAX) NULL,
    [LastValue]     NVARCHAR (MAX) NULL,
    [created_date]  DATETIME       CONSTRAINT [DF_MenuSystem_QuickRunParameter_created_date] DEFAULT (getdate()) NOT NULL,
    [modified_date] DATETIME       NULL,
    CONSTRAINT [PK_MenuSystem_QuickRunParameter] PRIMARY KEY CLUSTERED ([Id] ASC),
    CONSTRAINT [UQ_MenuSystem_QuickRunParameter] UNIQUE NONCLUSTERED ([ProcedureName] ASC, [ParameterName] ASC)
);


GO
CREATE OR ALTER TRIGGER MenuSystem.trgQuickRunParameter_modified_date
ON MenuSystem.QuickRunParameter AFTER UPDATE AS
BEGIN
    SET NOCOUNT ON;
    IF TRIGGER_NESTLEVEL(OBJECT_ID(N'MenuSystem.trgQuickRunParameter_modified_date')) > 1 RETURN;
    UPDATE tgt SET modified_date = GETDATE()
    FROM MenuSystem.QuickRunParameter tgt JOIN inserted i ON tgt.Id = i.Id;
END;
GO
-- --------------------------------------------------------------------------- Procedures
GO
-- Move an action one place within one of its categories.
CREATE OR ALTER PROCEDURE MenuSystem.MenuItemCategory_Move
    @ApplicationKey nvarchar(100), @MenuItemId int, @CategoryId int, @Delta int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @Delta NOT IN (-1, 1) THROW 50000, 'Move one place at a time.', 1;
    IF NOT EXISTS(SELECT 1 FROM MenuSystem.MenuItemCategory
        WHERE ApplicationKey = @ApplicationKey AND MenuItemId = @MenuItemId AND CategoryId = @CategoryId)
        THROW 50000, 'That menu item is not in that category.', 1;
    BEGIN TRANSACTION;
    WITH Ordered AS (
        SELECT placed.MenuItemId, ROW_NUMBER() OVER(ORDER BY placed.SortOrder, item.Title, placed.MenuItemId) AS Position
        FROM MenuSystem.MenuItemCategory placed
        JOIN MenuSystem.MenuItem item ON item.ApplicationKey = placed.ApplicationKey AND item.Id = placed.MenuItemId
        WHERE placed.ApplicationKey = @ApplicationKey AND placed.CategoryId = @CategoryId AND item.Retired = 0)
    UPDATE placed SET SortOrder = Ordered.Position
    FROM MenuSystem.MenuItemCategory placed
    JOIN Ordered ON Ordered.MenuItemId = placed.MenuItemId
    WHERE placed.ApplicationKey = @ApplicationKey AND placed.CategoryId = @CategoryId;
    DECLARE @mine int = (SELECT SortOrder FROM MenuSystem.MenuItemCategory
        WHERE ApplicationKey = @ApplicationKey AND MenuItemId = @MenuItemId AND CategoryId = @CategoryId);
    DECLARE @swapId int = (SELECT MenuItemId FROM MenuSystem.MenuItemCategory
        WHERE ApplicationKey = @ApplicationKey AND CategoryId = @CategoryId AND SortOrder = @mine + @Delta);
    IF @swapId IS NOT NULL
    BEGIN
        UPDATE MenuSystem.MenuItemCategory SET SortOrder = @mine
        WHERE ApplicationKey = @ApplicationKey AND CategoryId = @CategoryId AND MenuItemId = @swapId;
        UPDATE MenuSystem.MenuItemCategory SET SortOrder = @mine + @Delta
        WHERE ApplicationKey = @ApplicationKey AND CategoryId = @CategoryId AND MenuItemId = @MenuItemId;
    END;
    COMMIT TRANSACTION;
END;
GO
-- Remove one placement. The validation trigger refuses to remove an action's last category.
CREATE OR ALTER PROCEDURE MenuSystem.MenuItemCategory_Remove
    @ApplicationKey nvarchar(100), @MenuItemId int, @CategoryId int
AS
BEGIN
    SET NOCOUNT ON;
    DELETE MenuSystem.MenuItemCategory
    WHERE ApplicationKey = @ApplicationKey AND MenuItemId = @MenuItemId AND CategoryId = @CategoryId;
END;
GO
-- Place an action in a category, or move it within one. Safe to rerun.
CREATE OR ALTER PROCEDURE MenuSystem.MenuItemCategory_Set
    @ApplicationKey nvarchar(100), @MenuItemId int, @CategoryId int, @SortOrder int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @SortOrder IS NULL
        SELECT @SortOrder = ISNULL(MAX(SortOrder), 0) + 1 FROM MenuSystem.MenuItemCategory
        WHERE ApplicationKey = @ApplicationKey AND CategoryId = @CategoryId;
    UPDATE MenuSystem.MenuItemCategory SET SortOrder = @SortOrder
    WHERE ApplicationKey = @ApplicationKey AND MenuItemId = @MenuItemId AND CategoryId = @CategoryId;
    IF @@ROWCOUNT = 0
        INSERT MenuSystem.MenuItemCategory(ApplicationKey, MenuItemId, CategoryId, SortOrder)
        VALUES(@ApplicationKey, @MenuItemId, @CategoryId, @SortOrder);
END;
GO
-- Move a category one place among its siblings. Orders are densified first so that ties and
-- gaps in existing data (which sort by SortOrder then Title) cannot make a swap a no-op.
CREATE OR ALTER PROCEDURE MenuSystem.MenuItem_MoveCategory
    @ApplicationKey nvarchar(100), @Id int, @Delta int
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @Delta NOT IN (-1, 1) THROW 50000, 'Move one place at a time.', 1;
    DECLARE @ParentId int, @found bit = 0;
    SELECT @ParentId = ParentId, @found = 1 FROM MenuSystem.MenuItem
    WHERE ApplicationKey = @ApplicationKey AND Id = @Id AND Kind = 0 AND Retired = 0;
    IF @found = 0 THROW 50000, 'That category does not exist in this application.', 1;
    BEGIN TRANSACTION;
    WITH Ordered AS (
        SELECT Id, ROW_NUMBER() OVER(ORDER BY SortOrder, Title, Id) AS Position
        FROM MenuSystem.MenuItem
        WHERE ApplicationKey = @ApplicationKey AND Kind = 0 AND Retired = 0
            AND ((@ParentId IS NULL AND ParentId IS NULL) OR ParentId = @ParentId))
    UPDATE item SET SortOrder = Ordered.Position
    FROM MenuSystem.MenuItem item JOIN Ordered ON Ordered.Id = item.Id;
    DECLARE @mine int = (SELECT SortOrder FROM MenuSystem.MenuItem WHERE Id = @Id);
    DECLARE @swapId int = (SELECT Id FROM MenuSystem.MenuItem
        WHERE ApplicationKey = @ApplicationKey AND Kind = 0 AND Retired = 0
            AND ((@ParentId IS NULL AND ParentId IS NULL) OR ParentId = @ParentId)
            AND SortOrder = @mine + @Delta);
    IF @swapId IS NOT NULL
    BEGIN
        UPDATE MenuSystem.MenuItem SET SortOrder = @mine WHERE Id = @swapId;
        UPDATE MenuSystem.MenuItem SET SortOrder = @mine + @Delta WHERE Id = @Id;
    END;
    COMMIT TRANSACTION;
END;
GO
CREATE OR ALTER PROCEDURE MenuSystem.MenuItem_Restore
    @ApplicationKey nvarchar(100), @Id int
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE MenuSystem.MenuItem SET Retired = 0 WHERE ApplicationKey = @ApplicationKey AND Id = @Id;
END;
GO
CREATE OR ALTER PROCEDURE MenuSystem.MenuItem_Retire
    @ApplicationKey nvarchar(100), @Id int, @ReassignMembersTo int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    DECLARE @Kind int;
    SELECT @Kind = Kind FROM MenuSystem.MenuItem WHERE ApplicationKey = @ApplicationKey AND Id = @Id AND Retired = 0;
    IF @Kind IS NULL THROW 50000, 'That menu item does not exist in this application, or is already retired.', 1;
    BEGIN TRANSACTION;
    IF @Kind = 0
    BEGIN
        DECLARE @children int = (SELECT COUNT(*) FROM MenuSystem.MenuItem
            WHERE ApplicationKey = @ApplicationKey AND ParentId = @Id AND Retired = 0);
        DECLARE @members int = (SELECT COUNT(*) FROM MenuSystem.MenuItemCategory placed
            JOIN MenuSystem.MenuItem item ON item.ApplicationKey = placed.ApplicationKey AND item.Id = placed.MenuItemId
            WHERE placed.ApplicationKey = @ApplicationKey AND placed.CategoryId = @Id AND item.Retired = 0);
        IF (@children > 0 OR @members > 0) AND @ReassignMembersTo IS NULL
            THROW 50000, 'This category is not empty. Supply a category to move its contents to.', 1;
        IF @ReassignMembersTo IS NOT NULL
        BEGIN
            IF @ReassignMembersTo = @Id THROW 50000, 'Contents cannot be moved into the category being retired.', 1;
            IF NOT EXISTS(SELECT 1 FROM MenuSystem.MenuItem WHERE ApplicationKey = @ApplicationKey
                AND Id = @ReassignMembersTo AND Kind = 0 AND Retired = 0)
                THROW 50000, 'The destination must be a live category in this application.', 1;
            UPDATE MenuSystem.MenuItem SET ParentId = @ReassignMembersTo
            WHERE ApplicationKey = @ApplicationKey AND ParentId = @Id;
            -- Add the destination placement before removing the old one: the validation trigger
            -- refuses to remove an action's last category.
            INSERT MenuSystem.MenuItemCategory(ApplicationKey, MenuItemId, CategoryId, SortOrder)
            SELECT @ApplicationKey, moving.MenuItemId, @ReassignMembersTo, moving.SortOrder
            FROM MenuSystem.MenuItemCategory moving
            WHERE moving.ApplicationKey = @ApplicationKey AND moving.CategoryId = @Id
                AND NOT EXISTS(SELECT 1 FROM MenuSystem.MenuItemCategory existing
                    WHERE existing.MenuItemId = moving.MenuItemId AND existing.CategoryId = @ReassignMembersTo);
            DELETE MenuSystem.MenuItemCategory
            WHERE ApplicationKey = @ApplicationKey AND CategoryId = @Id;
        END;
    END
    ELSE
    BEGIN
        -- Favourites are preferences, not history: drop them. UsageLog rows stay.
        DELETE MenuSystem.UserFavourites WHERE ApplicationKey = @ApplicationKey AND MenuItemId = @Id;
    END;
    UPDATE MenuSystem.MenuItem SET Retired = 1 WHERE ApplicationKey = @ApplicationKey AND Id = @Id;
    COMMIT TRANSACTION;
END;
GO
CREATE OR ALTER PROCEDURE MenuSystem.MenuItem_Save
    @ApplicationKey nvarchar(100),
    @Id int = NULL OUTPUT,
    @Kind int,
    @Title nvarchar(200),
    @Description nvarchar(1000) = NULL,
    @IconKey nvarchar(200) = NULL,
    @Keywords nvarchar(1000) = NULL,
    @SortOrder int = 0,
    @ParentId int = NULL,
    @TargetKind int = 0,
    @TargetTypeName nvarchar(1000) = NULL,
    @AssemblyName nvarchar(300) = NULL,
    @ProcedureName nvarchar(517) = NULL,
    @ExecutablePath nvarchar(2000) = NULL,
    @Arguments nvarchar(2000) = NULL,
    @DefaultLaunchMode int = 0,
    @AllowNewInstance bit = 0
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NOT EXISTS(SELECT 1 FROM MenuSystem.Application WHERE ApplicationKey = @ApplicationKey AND SimpleMode = 1)
        THROW 50000, 'Restricted menu access is not implemented yet.', 1;
    IF NULLIF(LTRIM(RTRIM(@Title)), '') IS NULL THROW 50000, 'A menu item needs a title.', 1;
    IF @Kind NOT IN (0, 1) THROW 50000, 'Kind must be 0 (category) or 1 (action).', 1;
    IF @Kind = 1 AND @ParentId IS NOT NULL
        THROW 50000, 'Actions are placed through MenuItemCategory, not ParentId.', 1;
    IF @ParentId IS NOT NULL AND NOT EXISTS(SELECT 1 FROM MenuSystem.MenuItem
        WHERE ApplicationKey = @ApplicationKey AND Id = @ParentId AND Kind = 0)
        THROW 50000, 'The parent must be a category in this application.', 1;
    IF @Id IS NULL
    BEGIN
        INSERT MenuSystem.MenuItem(ApplicationKey, ParentId, Kind, Title, [Description], IconKey, Keywords,
            SortOrder, TargetKind, TargetTypeName, AssemblyName, ProcedureName, ExecutablePath, Arguments,
            DefaultLaunchMode, AllowNewInstance)
        VALUES(@ApplicationKey, @ParentId, @Kind, @Title, @Description, @IconKey, @Keywords,
            @SortOrder, @TargetKind, @TargetTypeName, @AssemblyName, @ProcedureName, @ExecutablePath, @Arguments,
            @DefaultLaunchMode, @AllowNewInstance);
        SET @Id = CONVERT(int, SCOPE_IDENTITY());
    END
    ELSE
    BEGIN
        IF NOT EXISTS(SELECT 1 FROM MenuSystem.MenuItem WHERE ApplicationKey = @ApplicationKey AND Id = @Id)
            THROW 50000, 'That menu item does not belong to this application.', 1;
        -- Changing Kind would invalidate existing placements or child categories; refuse it.
        IF EXISTS(SELECT 1 FROM MenuSystem.MenuItem WHERE Id = @Id AND Kind <> @Kind)
            THROW 50000, 'A category cannot become an action, or an action a category.', 1;
        IF @Id = @ParentId THROW 50000, 'A category cannot be its own parent.', 1;
        UPDATE MenuSystem.MenuItem SET ParentId = @ParentId, Title = @Title, [Description] = @Description,
            IconKey = @IconKey, Keywords = @Keywords, SortOrder = @SortOrder, TargetKind = @TargetKind,
            TargetTypeName = @TargetTypeName, AssemblyName = @AssemblyName, ProcedureName = @ProcedureName,
            ExecutablePath = @ExecutablePath, Arguments = @Arguments, DefaultLaunchMode = @DefaultLaunchMode,
            AllowNewInstance = @AllowNewInstance
        WHERE ApplicationKey = @ApplicationKey AND Id = @Id;
    END;
    SELECT @Id;
END;
GO
CREATE OR ALTER PROCEDURE MenuSystem.UsageLog_Prune
    @ApplicationKey nvarchar(100) = NULL,
    @BatchSize int = 1000,
    @MaxBatches int = 10
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF @BatchSize NOT BETWEEN 1 AND 10000 OR @MaxBatches NOT BETWEEN 1 AND 10000
        THROW 50000, 'Invalid pruning batch limits.', 1;
    DECLARE @batch int = 0, @rows int = 1, @now datetime = GETDATE();
    WHILE @batch < @MaxBatches AND @rows > 0
    BEGIN
        DELETE TOP (@BatchSize) usage
        FROM MenuSystem.UsageLog usage
        JOIN MenuSystem.Application app ON app.ApplicationKey = usage.ApplicationKey
        WHERE (@ApplicationKey IS NULL OR app.ApplicationKey = @ApplicationKey)
            AND app.UsageRetentionDays IS NOT NULL
            AND usage.created_date < DATEADD(day, -app.UsageRetentionDays, @now);
        SET @rows = @@ROWCOUNT;
        SET @batch += 1;
    END;
END;
GO
CREATE OR ALTER PROCEDURE MenuSystem.UsageLog_Record
    @UserId int, @ApplicationKey nvarchar(100), @MenuItemId int, @LaunchMode int,
    @SessionId uniqueidentifier, @MachineName nvarchar(128)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NOT EXISTS(SELECT 1 FROM MenuSystem.Application WHERE ApplicationKey = @ApplicationKey AND SimpleMode = 1)
        THROW 50000, 'Restricted menu access is not implemented yet.', 1;
    IF NOT EXISTS(SELECT 1 FROM MenuSystem.MenuItem WHERE Id = @MenuItemId AND ApplicationKey = @ApplicationKey AND Kind = 1)
        THROW 50000, 'The selected menu item does not belong to this application or cannot be launched.', 1;
    DECLARE @used datetime = GETDATE();
    INSERT MenuSystem.UsageLog(UserId, ApplicationKey, MenuItemId, LaunchMode, SessionId, MachineName, created_date)
    VALUES(@UserId, @ApplicationKey, @MenuItemId, @LaunchMode, @SessionId, @MachineName, @used);
    EXEC MenuSystem.UsageLog_Prune @ApplicationKey, 500, 1;
    SELECT @used;
END;
GO
CREATE OR ALTER PROCEDURE MenuSystem.UserFavourites_Set
    @UserId int, @ApplicationKey nvarchar(100), @MenuItemId int,
    @IsFavourite bit, @SortOrder int = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NOT EXISTS(SELECT 1 FROM MenuSystem.Application WHERE ApplicationKey = @ApplicationKey AND SimpleMode = 1)
        THROW 50000, 'Restricted menu access is not implemented yet.', 1;
    IF NOT EXISTS(SELECT 1 FROM MenuSystem.MenuItem WHERE Id = @MenuItemId AND ApplicationKey = @ApplicationKey AND Kind = 1)
        THROW 50000, 'The selected menu item does not belong to this application or cannot be launched.', 1;
    IF @SortOrder IS NOT NULL AND @SortOrder <= 0 THROW 50000, 'Favourite order must be positive.', 1;
    DECLARE @lockedUser int;
    BEGIN TRANSACTION;
    SELECT @lockedUser = Id FROM MenuSystem.[User] WITH(UPDLOCK, HOLDLOCK) WHERE Id = @UserId;
    IF @lockedUser IS NULL THROW 50000, 'Menu user is not registered.', 1;
    IF @IsFavourite = 0
        DELETE MenuSystem.UserFavourites WHERE UserId = @UserId AND MenuItemId = @MenuItemId;
    ELSE IF EXISTS(SELECT 1 FROM MenuSystem.UserFavourites WHERE UserId = @UserId AND MenuItemId = @MenuItemId)
    BEGIN
        IF @SortOrder IS NOT NULL UPDATE MenuSystem.UserFavourites SET SortOrder = @SortOrder WHERE UserId = @UserId AND MenuItemId = @MenuItemId;
    END
    ELSE
    BEGIN
        IF @SortOrder IS NULL SELECT @SortOrder = ISNULL(MAX(SortOrder), 0) + 1 FROM MenuSystem.UserFavourites WHERE UserId = @UserId AND ApplicationKey = @ApplicationKey;
        INSERT MenuSystem.UserFavourites(UserId, ApplicationKey, MenuItemId, SortOrder) VALUES(@UserId, @ApplicationKey, @MenuItemId, @SortOrder);
    END;
    COMMIT TRANSACTION;
END;
GO
CREATE OR ALTER PROCEDURE MenuSystem.UserSetting_Select
    @UserId int, @ApplicationKey nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT Name, Value FROM MenuSystem.UserSetting
    WHERE UserId = @UserId AND ApplicationKey = @ApplicationKey;
END;
GO
-- A null value clears the preference.
CREATE OR ALTER PROCEDURE MenuSystem.UserSetting_Set
    @UserId int, @ApplicationKey nvarchar(100), @Name nvarchar(64), @Value nvarchar(400) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NULLIF(LTRIM(RTRIM(@Name)), '') IS NULL THROW 50000, 'A setting needs a name.', 1;
    IF NOT EXISTS(SELECT 1 FROM MenuSystem.[User] WHERE Id = @UserId)
        THROW 50000, 'Menu user is not registered.', 1;
    IF @Value IS NULL
    BEGIN
        DELETE MenuSystem.UserSetting
        WHERE UserId = @UserId AND ApplicationKey = @ApplicationKey AND Name = @Name;
        RETURN;
    END;
    UPDATE MenuSystem.UserSetting SET Value = @Value
    WHERE UserId = @UserId AND ApplicationKey = @ApplicationKey AND Name = @Name;
    IF @@ROWCOUNT = 0
        INSERT MenuSystem.UserSetting(UserId, ApplicationKey, Name, Value)
        VALUES(@UserId, @ApplicationKey, @Name, @Value);
END;
GO
CREATE OR ALTER PROCEDURE MenuSystem.UserState_Select
    @UserId int, @ApplicationKey nvarchar(100)
AS
BEGIN
    SET NOCOUNT ON;
    IF NOT EXISTS(SELECT 1 FROM MenuSystem.Application WHERE ApplicationKey = @ApplicationKey AND SimpleMode = 1)
        THROW 50000, 'Restricted menu access is not implemented yet.', 1;
    IF NOT EXISTS(SELECT 1 FROM MenuSystem.[User] WHERE Id = @UserId)
        THROW 50000, 'Menu user is not registered.', 1;
    SELECT item.Id AS MenuItemId, favourite.SortOrder, recent.LastUsed
    FROM MenuSystem.MenuItem item
    LEFT JOIN MenuSystem.UserFavourites favourite ON favourite.UserId = @UserId AND favourite.MenuItemId = item.Id
    LEFT JOIN (
        SELECT MenuItemId, MAX(created_date) AS LastUsed FROM MenuSystem.UsageLog
        WHERE UserId = @UserId AND ApplicationKey = @ApplicationKey GROUP BY MenuItemId
    ) recent ON recent.MenuItemId = item.Id
    WHERE item.ApplicationKey = @ApplicationKey AND item.Kind = 1
        AND (favourite.MenuItemId IS NOT NULL OR recent.MenuItemId IS NOT NULL);
END;
GO
CREATE OR ALTER PROCEDURE MenuSystem.User_Open
    @ApplicationKey nvarchar(100), @IdentityKey nvarchar(256), @DisplayName nvarchar(256)
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;
    IF NOT EXISTS(SELECT 1 FROM MenuSystem.Application WHERE ApplicationKey = @ApplicationKey AND SimpleMode = 1)
        THROW 50000, 'This application must enable SimpleMode; restricted menu access is not implemented yet.', 1;
    IF NULLIF(LTRIM(RTRIM(@IdentityKey)), '') IS NULL OR NULLIF(LTRIM(RTRIM(@DisplayName)), '') IS NULL
        THROW 50000, 'A user identity and display name are required.', 1;
    DECLARE @id int;
    BEGIN TRANSACTION;
    SELECT @id = Id FROM MenuSystem.[User] WITH(UPDLOCK, HOLDLOCK) WHERE IdentityKey = @IdentityKey;
    IF @id IS NULL
    BEGIN
        INSERT MenuSystem.[User](IdentityKey, DisplayName) VALUES(@IdentityKey, @DisplayName);
        SET @id = CONVERT(int, SCOPE_IDENTITY());
    END
    ELSE UPDATE MenuSystem.[User] SET DisplayName = @DisplayName WHERE Id = @id AND DisplayName <> @DisplayName;
    COMMIT TRANSACTION;
    EXEC MenuSystem.UsageLog_Prune @ApplicationKey, 500, 1;
    SELECT @id;
END;
GO
SET NOEXEC OFF;
GO
-- Register an application before its first run, for example:
--   INSERT MenuSystem.Application(ApplicationKey, Title) VALUES(N'MyApp', N'My Application');
-- SimpleMode defaults to 1, which registers any Windows account that runs the application and
-- shows it the whole menu. The procedures refuse to run with SimpleMode = 0, so restricted
-- per-user menus fail closed until that feature exists.
