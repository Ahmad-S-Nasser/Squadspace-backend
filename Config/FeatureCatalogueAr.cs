namespace RafeeqyNotes.Api.Config
{
    /// <summary>
    /// Arabic names and descriptions for the feature catalogue.
    /// </summary>
    /// <remarks>
    /// A separate file, keyed by the SAME feature keys as <see cref="FeatureCatalogue"/>, rather
    /// than extra properties on every descriptor. Two reasons:
    ///
    /// 1. <c>FeatureCatalogue.All()</c> stays readable. Adding NameAr and DescriptionAr inline
    ///    would double the width of every entry in the one list a developer edits when a feature
    ///    ships, and the English list is the one that must never become a chore to maintain.
    ///
    /// 2. A missing translation is then a missing dictionary entry, which falls back to English
    ///    automatically. A feature that ships without Arabic copy still appears - in English -
    ///    rather than appearing blank or blocking the release.
    ///
    /// WHY THIS EXISTS AT ALL: the Arabic marketing site listed every feature in English, because
    /// this catalogue is the single source of truth and had only one language. Feature names are
    /// exactly the words someone types into a search engine, so an Arabic page describing its
    /// features in English is an Arabic page that cannot be found in Arabic.
    /// </remarks>
    public static class FeatureCatalogueAr
    {
        /// <summary>Category display names, keyed by the English constant.</summary>
        public static readonly Dictionary<string, string> Categories = new()
        {
            [FeatureCatalogue.CategoryCore] = "الأساسيات",
            [FeatureCatalogue.CategoryPlanning] = "التخطيط",
            [FeatureCatalogue.CategoryTracking] = "الوقت والتتبّع",
            [FeatureCatalogue.CategoryReporting] = "التقارير",
            [FeatureCatalogue.CategoryCollaboration] = "التعاون",
            [FeatureCatalogue.CategoryCode] = "الكود",
            [FeatureCatalogue.CategoryAutomation] = "الأتمتة",
            [FeatureCatalogue.CategoryData] = "البيانات والترحيل",
            [FeatureCatalogue.CategorySecurity] = "الأمان والإدارة",
        };

        /// <summary>Feature name and description, keyed by feature key.</summary>
        public static readonly Dictionary<string, (string Name, string Description)> Features = new()
        {
            // ---- core ----
            ["projects"] = ("المشاريع واللوحات",
                "نظّم العمل في مشاريع ولوحات وملاحظات، مع أعضاء لكل مشروع."),
            ["tasks"] = ("المهام",
                "مسؤولون ومراجعون وأولويات وفئات ومواعيد تسليم وتقديرات واعتماديات."),
            ["kanban"] = ("لوحة كانبان",
                "اسحب العمل بين الأعمدة، مع عرض لحظي لما ينشغل به الجميع."),
            ["custom-statuses"] = ("حالات قابلة للتخصيص",
                "عرّف أعمدة سير العمل الخاصة بمؤسستك، وحدّد أيها يُحتسب منجزًا."),
            ["custom-fields"] = ("حقول مخصّصة",
                "أضِف حقولك الخاصة إلى المهام وأصدر تقارير عنها."),
            ["search"] = ("البحث",
                "بحث في النص الكامل عبر الملاحظات والمهام داخل مؤسستك."),

            // ---- planning ----
            ["sprints"] = ("السبرنتات",
                "خطّط على شكل سبرنتات مع تتبّع السرعة ومخطط الإنجاز."),
            ["gantt"] = ("جانت والجدول الزمني",
                "اطّلع على الجداول والتداخلات والاعتماديات عبر المشروع."),
            ["dependencies"] = ("اعتماديات المهام",
                "اربط العمل المُعيق واعرف ما ينتظر ماذا."),
            ["meetings"] = ("الاجتماعات",
                "نظّم اجتماعات بمشاركين وجداول أعمال ومرفقات."),
            ["calendar"] = ("دعوات التقويم (.ics)",
                "تصل الاجتماعات كدعوات تقويم حقيقية في Outlook وGmail وتقويم Apple."),

            // ---- time ----
            ["timer"] = ("مؤقّت المهام",
                "ابدأ المؤقّت على مهمة وأوقفه؛ الوقت يُقاس على الخادم لا يُكتب من الذاكرة."),
            ["manual-time"] = ("إدخال الوقت يدويًا",
                "سجّل وقتًا نسيت تتبّعه، ويُعلَّم على أنه مُدخل لا مقيس."),
            ["estimates"] = ("التقديرات مقابل الفعلي",
                "قارن ما كان متوقّعًا أن يستغرقه العمل بما استغرقه فعلًا."),

            // ---- reporting ----
            ["dashboard"] = ("لوحة التحكم",
                "توزيع الأحمال واتجاه الإنجاز ومخطط الإنجاز ونظرة كانبان عبر المشاريع."),
            ["report-builder"] = ("منشئ التقارير",
                "ابنِ تقاريرك: صفِّ بشروط، وجمّع، وقِس، واعرضها رسمًا بيانيًا أو جدولًا."),
            ["saved-reports"] = ("تقارير محفوظة ومشتركة",
                "احفظ تقريرًا، وشاركه مع مؤسستك، وثبّته على لوحة تحكمك."),
            ["report-export"] = ("تصدير التقارير",
                "نزّل أي تقرير بصيغة CSV."),

            // ---- collaboration ----
            ["notes"] = ("ملاحظات غنية",
                "ملاحظات منسّقة بوسوم ومساهمين وإمكانية مشاركة عامة."),
            ["comments"] = ("التعليقات والنشاط",
                "ناقش داخل المهمة، مع سجل كامل لما تغيّر ومن غيّره."),
            ["chat"] = ("محادثات الفريق",
                "رسائل مباشرة وجماعية بجوار العمل."),
            ["whiteboard"] = ("السبورة",
                "لوحة مشتركة للرسم والتخطيط، مع تعاون لحظي."),
            ["notifications"] = ("الإشعارات",
                "إشعارات داخل التطبيق وبالبريد للإسناد والإشارات والتغييرات."),

            // ---- code ----
            ["git"] = ("مستودعات Git",
                "مستودعات مستضافة ذاتيًا بفروع والتزامات وتصفّح للملفات وطلبات دمج."),
            ["github"] = ("الربط مع GitHub",
                "اربط حساب GitHub للعمل مع مستودعاتك الحالية."),

            // ---- automation ----
            ["automation-rules"] = ("قواعد الأتمتة",
                "حين يقع حدث، نفّذ إجراءً: إسناد أو نقل أو تعليق أو إشعار."),
            ["scheduled-reports"] = ("تقارير مجدولة",
                "استقبل تقريرًا بالبريد كل صباح أو أسبوع أو شهر دون أن يشغّله أحد."),

            // ---- data ----
            ["export"] = ("تصدير البيانات",
                "صدّر مشاريعك ولوحاتك وملاحظاتك ومهامك بصيغة CSV في أي وقت."),
            ["import"] = ("الاستيراد من Jira وCSV",
                "انقل مساحة عمل كاملة، مع مطابقة الحالات وتشغيل تجريبي واستيراد قابل للاستئناف."),
            ["attachments"] = ("المرفقات",
                "أرفِق ملفات بالمهام والملاحظات والاجتماعات."),

            // ---- security ----
            ["organizations"] = ("المؤسسات والأدوار",
                "أدوار المالك والمسؤول والعضو والمطّلع، مع تقييد كل إجراء بنطاق المؤسسة."),
            ["invitations"] = ("الدعوات",
                "ادعُ زملاءك بالبريد الإلكتروني مع تحديد الدور."),
            ["sso"] = ("الدخول الموحّد",
                "سجّل الدخول بدليل Google Workspace أو Microsoft Entra الخاص بشركتك، مع إمكانية فرضه."),
            ["audit"] = ("سجل النشاط",
                "من غيّر ماذا، ومتى، على كل مهمة."),
        };

        /// <summary>
        /// Returns the catalogue in the requested language.
        /// </summary>
        /// <remarks>
        /// Anything other than "ar" returns the English list unchanged, and a feature with no
        /// Arabic entry keeps its English name and description rather than disappearing.
        /// </remarks>
        public static List<FeatureDescriptor> Localize(List<FeatureDescriptor> features, string lang)
        {
            if (!string.Equals(lang, "ar", StringComparison.OrdinalIgnoreCase)) return features;

            return features.Select(f =>
            {
                var translated = Features.TryGetValue(f.Key ?? string.Empty, out var copy);

                return new FeatureDescriptor
                {
                    Key = f.Key,
                    // The category is translated too - it is a visible heading on the page.
                    Category = Categories.TryGetValue(f.Category ?? string.Empty, out var cat)
                        ? cat
                        : f.Category,
                    Name = translated ? copy.Name : f.Name,
                    Description = translated ? copy.Description : f.Description,
                    EntitlementKey = f.EntitlementKey,
                    SortOrder = f.SortOrder,
                };
            }).ToList();
        }
    }
}
