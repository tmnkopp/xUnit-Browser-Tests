using OpenQA.Selenium.Chrome;
using System;
using System.Collections.Generic;
using System.Configuration; 
using System.Linq; 
using System.Reflection;
using OpenQA.Selenium.Support.UI;
using OpenQA.Selenium;
using System.Text.RegularExpressions;
using System.IO;
using Serilog;
using CyberScope.Automator.Providers;
using System.Threading;

namespace CyberScope.Automator
{
    #region ENUMS 
    public enum UserContext
    {
        Admin,
        Agency,
        CB
    }
    public enum TestResult
    {
        Pass,
        Fail 
    }
    #endregion
    public class CsDriverService
    {
        #region PROPS 
        public UserContext UserContext { get; set; } = UserContext.Agency; 
        public ILogger Logger;  
        private ChromeDriver _driver;  
        public void DisposeDriverService() { 
            Driver.Quit();
        }
        public ChromeDriver Driver
        {
            get
            {
                if (_driver == null)
                {
                    var codeBaseUrl = new Uri(Assembly.GetExecutingAssembly().CodeBase);
                    var codeBasePath = Uri.UnescapeDataString(codeBaseUrl.AbsolutePath);
                    var dirPath = Path.GetDirectoryName(codeBasePath);
                    var chromedriverpath =  SettingsProvider.appSettings[$"chromedriverpath"];
                    if (string.IsNullOrEmpty(chromedriverpath)) 
                        chromedriverpath = $"{dirPath}\\Selenium\\";
                    ChromeOptions options = new ChromeOptions();
                    var chromeDriverService = ChromeDriverService.CreateDefaultService(chromedriverpath);
                    chromeDriverService.HideCommandPromptWindow = true;
                    chromeDriverService.SuppressInitialDiagnosticInformation = true;
                    var args = SettingsProvider.ChromeOptions;
                    foreach (var item in args.EmptyIfNull()) 
                        options.AddArgument(item);
                    _driver = new ChromeDriver(chromeDriverService, options);
                    this.defaultWait = new WebDriverWait(_driver, TimeSpan.FromSeconds(1)); 
                }
                return _driver;
            }
        }
        #endregion
        #region CTOR 
        WebDriverWait defaultWait;
        public CsDriverService(ILogger Logger)
        { 
            this.Logger = Logger;
        } 
        #endregion
        #region Events 
        public class DriverServiceEventArgs : EventArgs
        {
            public CsDriverService DriverService { get; set; }
            public DataCallSection Section { get; set; }
            public ChromeDriver Driver { get; set; }
            public DriverServiceEventArgs(CsDriverService driverService)
            {
                this.DriverService = driverService;
                this.Driver = driverService.Driver;
            } 
        } 
        public event EventHandler<DriverServiceEventArgs> OnApplicationError;
        protected virtual void ApplicationError(DriverServiceEventArgs e)
        { 
            OnApplicationError?.Invoke(this, e);
        }
        public event EventHandler<DriverServiceEventArgs> OnSectionComplete;
        protected virtual void SectionComplete(DriverServiceEventArgs e)
        {
            OnSectionComplete?.Invoke(this, e);
        }
        #endregion
        #region METHODS
        #region METHODS: Control ACCESSORS 
        internal IEnumerable<IAutomator> PageControlCollection() {
            var automators = new List<IAutomator>();
            var driver = this.Driver;
            var controlLocators = SettingsProvider.ControlLocators.EmptyIfNull();
            foreach (ControlLocator controlLocator in controlLocators)
            {
                var eles = (from e in driver.FindElements(By.XPath($"{controlLocator.Locator}"))
                           where e.Displayed==true && e.Enabled==true select e).ToList();
                if (eles.Count > 0)
                {
                    var type = Assm.GetTypes().Where(t => t.Name == controlLocator.Type).FirstOrDefault();
                    IAutomator obj = (IAutomator)Activator.CreateInstance(Type.GetType($"{type.FullName}"));

                    string ValueSetterType = (!string.IsNullOrWhiteSpace(controlLocator.ValueSetterTypes)) ? controlLocator.ValueSetterTypes : ".*";

                    obj.ContainerSelector = $" #{GetElementID(controlLocator.Selector)} ";
                    obj.ValueSetters = (from vs in obj.ValueSetters
                                        where Regex.IsMatch(vs.GetType().Name, ValueSetterType)
                                        select vs).ToList(); 
                    automators.Add(obj); 
                }
            }
            return automators;
        }
        public bool ElementExists(By Selector) {
            return (this.Driver.FindElements(Selector).Count() > 0);
        }
        #endregion
        #region METHODS: NAV
        public CsDriverService ToUrl(string url)
        {
            var @base = SettingsProvider.appSettings[$"CSTargerUrl"]; 
            url = url.Replace("~", @base);
            this.Driver.Navigate().GoToUrl(url);
            return this;
        }
        public CsDriverService CsConnect(UserContext userContext)
        {
            var url = SettingsProvider.appSettings[$"CSTargerUrl"];
            var sc = new SessionContext()
            {
                Driver = this.Driver ,
                Logger = this.Logger ,
                userContext = userContext
            }; 
            var IConnectType = (from t in Assm.GetTypes()
                           where typeof(IConnect).IsAssignableFrom(t) 
                           && Regex.IsMatch(url, t.GetCustomAttribute<ConnectorMeta>()?.Selector ?? "^$")
                           select t).FirstOrDefault() ?? typeof(DefaultCsConnector);
            IConnect obj = (IConnect)Activator.CreateInstance(Type.GetType($"{IConnectType.FullName}"));
            obj.Connect(sc); 
            return this;
        }
        public CsDriverService ToTab(string TabText, bool Launch = true)
        { 
            var driver = this.Driver;
            IWebElement ele;
            WebDriverWait wait;
            if (!driver.Url.Contains("ReporterHome.aspx"))
            {
                wait = new WebDriverWait(driver, TimeSpan.FromSeconds(2));
                ele = wait.Until(drv => drv.FindElement(By.XPath($"//*[contains(@id, 'ctl00_ImageButton1')]")));
                ele?.Click();
            }
            wait = new WebDriverWait(driver, TimeSpan.FromSeconds(2));
            var eles = wait.Until(drv => drv.FindElements(By.XPath($"//*[contains(@id, '_Surveys')]//*[contains(@class, 'rtsTxt')]")))?.Reverse();
            ele = (from e in eles where Regex.IsMatch(e.Text, TabText) || e.Text.Contains(TabText) select e).FirstOrDefault();
            ele?.Click(); 
            ele = wait.Until(drv => drv.FindElement(By.XPath($"//a[contains(@id, '_ctl04_hl_Launch')]")));
            ele?.Click();
            return this;
        }
        public CsDriverService ToSection(DataCallSection Section)  { 
            SelectElement se = new SelectElement(this.Driver.FindElementByCssSelector("*[id*='_ddl_Sections']")); 
            se?.Options.Where(o => o.Text.Contains(Section?.SectionText)).FirstOrDefault()?.Click(); 
            return this;
        }
        public CsDriverService ToSection(Func<DataCallSection, bool> Predicate) { 
            var section = this.Sections().Where(Predicate).FirstOrDefault();
            this.ToSection(section);
            return this; 
        }
        public CsDriverService ToSection(int Index)
        {
            var driver = this.Driver;
            SelectElement se = new SelectElement(driver.FindElementByCssSelector("*[id*='_ddl_Sections']"));
            if (Index < 0)
                Index = se.Options.Count() - 1;
            se.SelectByIndex(Index);
            return this;
        }
        #endregion
        #region METHODS: FIELD ACCESSORS
        public IWebElement GetElement(By By)
        {
            WebDriverWait wait = new WebDriverWait(this.Driver, TimeSpan.FromSeconds(1));
            return (from e in wait.Until(dvr => dvr.FindElements(By))
                    where e.Enabled && e.Displayed
                    select e).FirstOrDefault();
        } 
        public string GetElementValue(By By)
        {
            IWebElement element = GetElement(By);
            return element?.Text;
        } 
        public CsDriverService SetFieldValue(By By, string value)
        {
            IWebElement element = GetElement(By);
            element?.Clear();
            element?.SendKeys(value);
            return this;
        }
        #endregion
        #region METHODS: Section ACCESSORS
        public IEnumerable<DataCallSection> Sections() { 
            var driver = this.Driver;
            WebDriverWait wait = new WebDriverWait(driver, TimeSpan.FromSeconds(1));
            IReadOnlyCollection<IWebElement> ele;
            ele = wait.Until(drv => drv.FindElements(By.XPath($"//*[contains(@id, '_ddl_Sections')]/option")));
            var groups = (from e in ele
                            select new DataCallSection
                            {
                                URL = e.GetAttribute("value"),
                                SectionText = e.Text
                            }).ToList(); 
            return groups;  
        }
        public CsDriverService ApplyValidationAttempt(ValidationAttempt va, Action Assertion) {
            var ds = this; 
            var assms = AppDomain.CurrentDomain.GetAssemblies();
            var ElementValueProviders = (from assm in assms
                                         from t in assm.GetTypes()
                    where typeof(IElementValueProvider).IsAssignableFrom(t) && t.IsClass
                    select t).ToList();

            Type answerProvider = typeof(ElementValueProvider);
            ElementValueProviders.ForEach(t => {
                var attr = t.GetCustomAttribute<ElementValueProviderMeta>(false);
                if (!string.IsNullOrEmpty(attr?.XpathMatch))
                {
                    var e = this.GetElement(By.XPath(attr.XpathMatch)); 
                    if (e != null) answerProvider = t;
                } 
            }); 

            IElementValueProvider obj = (IElementValueProvider)Activator.CreateInstance(answerProvider);
            ((IElementValueProvider)obj).Populate(ds);
            
            string attempt = ((IElementValueProvider)obj).Eval<string>(va.ErrorAttemptExpression);
            var Defaults = new DefaultInputProvider(ds.Driver).DefaultValues;
            Defaults.Add(va.MetricXpath, attempt);
            
            var sc = new SessionContext(ds.Logger, ds.Driver, Defaults);
            var pcc = ds.PageControlCollection().EmptyIfNull();
            ds.FismaFormEnable();
            
            string id = Utils.ExtractContainerId(ds.Driver, va.MetricXpath);
            foreach (IAutomator control in pcc)
            {
                if (!string.IsNullOrEmpty(id))
                    ((IAutomator)control).ContainerSelector = $"#{id} ";
                ((IAutomator)control).Automate(sc);
            }
            ds.FismaFormSave();  
            Assertion();
            return this;
        }
        public CsDriverService InitSections(Func<DataCallSection, bool> SectionGroupPredicate )
        { 
            SessionContext sessionContext = new SessionContext() { 
                Driver = this.Driver
                , Logger = this.Logger
                , Defaults = new DefaultInputProvider(this.Driver).DefaultValues 
            };
            foreach (DataCallSection section in this.Sections().Where(SectionGroupPredicate))
            {
                var appargs = new DriverServiceEventArgs(this);
                appargs.Section = section;
                this.ToSection(section);
                this.FismaFormEnable();
                foreach (IAutomator control in this.PageControlCollection().EmptyIfNull())
                    ((IAutomator)control).Automate(sessionContext);
                if (this.Driver.PageSource.Contains("Server Error in '/' Application")) 
                    ApplicationError(appargs);
                this.FismaFormSave(); 
                this.LogScreenshot(section.SectionText);
                SectionComplete(appargs);
            }
            return this;
        }
        public void LogScreenshot(string log)
        {
            var ssdir = SettingsProvider.appSettings[$"ScreenshotLogDir"];
            if (!string.IsNullOrWhiteSpace(ssdir))
            { 
                ((IJavaScriptExecutor)this.Driver).ExecuteScript("scroll(0, -250)");  
                var uri = (this.Driver.Url.Contains("?")) ? this.Driver.Url.Substring(1, this.Driver.Url.IndexOf("?")) : this.Driver.Url;
                uri = (from s in uri.Split('/') where s.Contains(".") select s).FirstOrDefault();
                uri = Regex.Replace(uri, @"[^\w\d_]", "");
                uri = (uri.Length >= 50) ? uri.Substring(1, 50) : uri;
                log = Regex.Replace(log, @"[^\w\d]", "");
                log = (log.Length >= 50) ? log.Substring(1, 50) : log;
                ssdir = ssdir.Replace("{log}", log);
                ssdir = ssdir.Replace("{uri}", uri);
                ssdir = ssdir.Replace("{date}", DateTime.Now.ToString("yyyy_MM_dd"));
                Screenshot ss = ((ITakesScreenshot)this.Driver).GetScreenshot();
                ss.SaveAsFile($"{ssdir}", ScreenshotImageFormat.Png);
            }
        }
        public CsDriverService OpenTab() { 
            string url = this.Driver.Url;
            ((IJavaScriptExecutor)this.Driver).ExecuteScript("window.open();");
            var handles = this.Driver.WindowHandles;
            this.Driver.SwitchTo().Window(handles[this.Driver.WindowHandles.Count - 1]);
            this.Driver.Navigate().GoToUrl($"{url}"); 
            return this;
        }
        public CsDriverService CloseTab()
        {
            var handles = this.Driver.WindowHandles;
            ((IJavaScriptExecutor)this.Driver).ExecuteScript("window.close();");
            this.Driver.SwitchTo().Window(handles[this.Driver.WindowHandles.Count - 1]);
            return this;
        }
        #endregion
        #region METHODS: FismaForm ACCESSORS 
        public CsDriverService FismaFormEnable()
        {
            string btntext = "_btnEdit";
            WebDriverWait wait = new WebDriverWait(this.Driver, TimeSpan.FromSeconds(.15));
            var eles = wait.Until(drv =>
                drv.FindElements(By.XPath($"//td[contains(@class, 'ButtonDiv')]//*[contains(@id, '{btntext}')]")));
            (from el in eles where el.Displayed && el.Enabled select el).FirstOrDefault()?.Click();
            return this;
        }
        public CsDriverService FismaFormCancel()
        {
            return this.FismaFormEnable();
        }
        public CsDriverService FismaFormSave()
        {
            string btntext = "_btnSave";
            WebDriverWait wait = new WebDriverWait(this.Driver, TimeSpan.FromSeconds(.15));
            var eles = wait.Until(drv =>
                drv.FindElements(By.XPath($"//td[contains(@class, 'ButtonDiv')]//*[contains(@id, '{btntext}')]")));
            (from el in eles where el.Displayed && el.Enabled select el).FirstOrDefault()?.Click();  
            return this; 
        }
        public bool FismaFormValidates() {
            this.ToSection(-1);
            LogScreenshot("FismaFormValidates");
            var success = new WebDriverWait(this.Driver, TimeSpan.FromSeconds(5))
            .Until(dvr => dvr.FindElements(By.CssSelector("#ctl00_ContentPlaceHolder1_lblSuccessInfo")));
            if (success.Count() > 0){
                return success[0].Text.Contains("Your form has been validated and contains no errors.");
            }    
            this.Logger.Warning($"FismaForm InValid");
            return false;
        }
        #endregion
        #region METHODS: PRIV
        public string GetElementID(string XPathSelector) {
            string id = "";
            if (this.Driver.FindElements(By.XPath(XPathSelector)).Count > 0)
                id = this.Driver.FindElement(By.XPath(XPathSelector)).GetAttribute("id"); 
            return id;
        }
        #endregion
        #endregion
    }
}