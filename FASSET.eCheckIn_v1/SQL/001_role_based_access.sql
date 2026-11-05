/* ------------------------------------------------------------------
   e-Check-in: role-based report access (review action A1, decision D2)

   Run ONCE on fasset_e_checkIn_db BEFORE deploying the matching code.
   Safe to run again: every step checks first.

   Roles:
     Admin     - ICT. Full access (existing users become Admin).
     Executive - CEO's office. Reports for every department.
     LLC       - Reports for the departments listed for them below.
     Manager   - Reports for their own department(s) only.
------------------------------------------------------------------- */
USE fasset_e_checkIn_db;
GO

-- 1. Role on each login. Existing rows (e.g. SuperUser) become Admin.
IF COL_LENGTH('dbo.Admins', 'Role') IS NULL
BEGIN
    ALTER TABLE dbo.Admins
        ADD Role VARCHAR(20) NOT NULL
            CONSTRAINT DF_Admins_Role DEFAULT ('Admin') WITH VALUES;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Admins_Role')
BEGIN
    ALTER TABLE dbo.Admins
        ADD CONSTRAINT CK_Admins_Role CHECK (Role IN ('Admin', 'Executive', 'LLC', 'Manager'));
END
GO

-- 2. Which departments an LLC member or Manager may see.
IF OBJECT_ID('dbo.AdminDepartmentScope', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AdminDepartmentScope (
        Username     VARCHAR(100) NOT NULL,
        DepartmentId INT          NOT NULL,
        CONSTRAINT PK_AdminDepartmentScope PRIMARY KEY (Username, DepartmentId)
    );
END
GO

-- Foreign keys are added separately so a column-type difference in an
-- older Admins/Departments table doesn't stop the rest of the script.
BEGIN TRY
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_AdminDepartmentScope_Departments')
        ALTER TABLE dbo.AdminDepartmentScope
            ADD CONSTRAINT FK_AdminDepartmentScope_Departments
            FOREIGN KEY (DepartmentId) REFERENCES dbo.Departments (Id);
END TRY
BEGIN CATCH
    PRINT 'Skipped FK to Departments: ' + ERROR_MESSAGE();
END CATCH
GO

BEGIN TRY
    IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_AdminDepartmentScope_Admins')
        ALTER TABLE dbo.AdminDepartmentScope
            ADD CONSTRAINT FK_AdminDepartmentScope_Admins
            FOREIGN KEY (Username) REFERENCES dbo.Admins (Username)
            ON DELETE CASCADE ON UPDATE CASCADE;
END TRY
BEGIN CATCH
    PRINT 'Skipped FK to Admins: ' + ERROR_MESSAGE();
END CATCH
GO

-- 3. Check: everyone's role and departments.
SELECT a.Username, a.Role,
       STRING_AGG(d.DepartmentName, ', ') AS Departments
FROM dbo.Admins a
LEFT JOIN dbo.AdminDepartmentScope s ON s.Username = a.Username
LEFT JOIN dbo.Departments d ON d.Id = s.DepartmentId
GROUP BY a.Username, a.Role
ORDER BY a.Role, a.Username;
GO

/* ------------------------------------------------------------------
   Day-to-day: new users are easiest to create in the app at
   Account/CreateAdmin (Admin only), which sets role and departments.

   To change an existing user by hand:

   -- Make someone a Manager of Finance
   UPDATE dbo.Admins SET Role = 'Manager' WHERE Username = 'jdoe';
   INSERT INTO dbo.AdminDepartmentScope (Username, DepartmentId)
   SELECT 'jdoe', Id FROM dbo.Departments WHERE DepartmentName = 'Finance';

   -- Give the CEO's office everything
   UPDATE dbo.Admins SET Role = 'Executive' WHERE Username = 'ceo.office';

   Changes take effect within 5 minutes (cached), or at the user's
   next login, or immediately after an app pool recycle.
------------------------------------------------------------------- */
