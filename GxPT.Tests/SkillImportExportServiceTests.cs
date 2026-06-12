using System;
using System.IO;
using System.Text;
using GxPT;
using Ionic.Zip;
using Xunit;

namespace GxPT.Tests
{
    public sealed class SkillImportExportServiceTests : IDisposable
    {
        private readonly string _root;

        public SkillImportExportServiceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_skillio_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        // Creates a skill folder (SKILL.md + a script) under <root>/source/<slug> and returns its Skill.
        private Skill WriteSkill(string slug, string description)
        {
            string dir = Path.Combine(Path.Combine(_root, "source"), slug);
            Directory.CreateDirectory(Path.Combine(dir, "scripts"));
            File.WriteAllText(Path.Combine(dir, "SKILL.md"),
                "---\nname: " + slug + "\ndescription: " + description + "\n---\n\nBody.\n",
                new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "scripts", "run.bat"), "@echo off\n");
            return new Skill(slug, slug, description, dir, Path.Combine(dir, "SKILL.md"), SkillSource.User);
        }

        [Fact]
        public void Export_then_import_round_trips_the_skill_folder()
        {
            Skill skill = WriteSkill("demo-skill", "Test skill.");
            string archive = Path.Combine(_root, "demo-skill.gxsk");
            SkillImportExportService.ExportSkill(skill, archive);
            Assert.True(File.Exists(archive));

            string target = Path.Combine(_root, "target");
            string slug = SkillImportExportService.ImportSkill(archive, target, null);

            Assert.Equal("demo-skill", slug);
            Assert.True(File.Exists(Path.Combine(target, "demo-skill", "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(target, "demo-skill", "scripts", "run.bat")));
        }

        [Fact]
        public void Import_flat_archive_derives_slug_from_frontmatter_name()
        {
            string archive = Path.Combine(_root, "anything.zip");
            using (var zip = new ZipFile())
            {
                zip.AddEntry("SKILL.md", "---\nname: Flat Skill\ndescription: d.\n---\n\nBody.\n");
                zip.Save(archive);
            }

            string target = Path.Combine(_root, "target");
            string slug = SkillImportExportService.ImportSkill(archive, target, null);

            Assert.Equal("flat-skill", slug);
            Assert.True(File.Exists(Path.Combine(target, "flat-skill", "SKILL.md")));
        }

        [Fact]
        public void Import_existing_skill_honors_overwrite_confirmation()
        {
            Skill skill = WriteSkill("dup", "v1.");
            string archive = Path.Combine(_root, "dup.gxsk");
            SkillImportExportService.ExportSkill(skill, archive);

            string target = Path.Combine(_root, "target");
            SkillImportExportService.ImportSkill(archive, target, null);
            string marker = Path.Combine(target, "dup", "marker.txt");
            File.WriteAllText(marker, "keep");

            // Declined: returns null, existing folder untouched.
            string declined = SkillImportExportService.ImportSkill(archive, target,
                delegate(string s) { Assert.Equal("dup", s); return false; });
            Assert.Null(declined);
            Assert.True(File.Exists(marker));

            // Confirmed: the folder is replaced wholesale, so the stray marker is gone.
            string confirmed = SkillImportExportService.ImportSkill(archive, target,
                delegate(string s) { return true; });
            Assert.Equal("dup", confirmed);
            Assert.False(File.Exists(marker));
            Assert.True(File.Exists(Path.Combine(target, "dup", "SKILL.md")));
        }

        [Fact]
        public void Import_archive_without_skill_md_throws()
        {
            string archive = Path.Combine(_root, "noskill.zip");
            using (var zip = new ZipFile())
            {
                zip.AddEntry("readme.txt", "not a skill");
                zip.Save(archive);
            }

            Assert.Throws<InvalidDataException>(delegate
            {
                SkillImportExportService.ImportSkill(archive, Path.Combine(_root, "target"), null);
            });
        }

        [Fact]
        public void Import_skill_without_description_throws()
        {
            string archive = Path.Combine(_root, "nodesc.gxsk");
            using (var zip = new ZipFile())
            {
                zip.AddEntry("bad-skill/SKILL.md", "---\nname: Bad\n---\n\nBody.\n");
                zip.Save(archive);
            }

            Assert.Throws<InvalidDataException>(delegate
            {
                SkillImportExportService.ImportSkill(archive, Path.Combine(_root, "target"), null);
            });
        }

        [Fact]
        public void Import_archive_with_two_skills_throws()
        {
            string archive = Path.Combine(_root, "two.gxsk");
            using (var zip = new ZipFile())
            {
                zip.AddEntry("one/SKILL.md", "---\ndescription: d.\n---\n");
                zip.AddEntry("two/SKILL.md", "---\ndescription: d.\n---\n");
                zip.Save(archive);
            }

            Assert.Throws<InvalidDataException>(delegate
            {
                SkillImportExportService.ImportSkill(archive, Path.Combine(_root, "target"), null);
            });
        }

        [Fact]
        public void ArchiveContainsSkill_detects_skill_archives_and_rejects_others()
        {
            Skill skill = WriteSkill("probe", "Probe.");
            string skillArchive = Path.Combine(_root, "probe.gxsk");
            SkillImportExportService.ExportSkill(skill, skillArchive);
            Assert.True(SkillImportExportService.ArchiveContainsSkill(skillArchive));

            string convArchive = Path.Combine(_root, "conv.zip");
            using (var zip = new ZipFile())
            {
                zip.AddEntry("conversation1.json", "{}");
                zip.Save(convArchive);
            }
            Assert.False(SkillImportExportService.ArchiveContainsSkill(convArchive));
        }
    }
}
