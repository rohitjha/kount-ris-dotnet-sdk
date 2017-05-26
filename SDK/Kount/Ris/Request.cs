//-----------------------------------------------------------------------
// <copyright file="Request.cs" company="Keynetics Inc">
//     Copyright Keynetics. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
namespace Kount.Ris
{
    using Kount.Log.Binding;
    using Kount.Log.Factory;
    using Kount.Util;
    using System;
    using System.Collections;
    using System.Configuration;
    using System.IO;
    using System.Net;
    using System.Reflection;
    using System.Security.Cryptography.X509Certificates;
    using System.Text;
    using System.Web;
    using System.Xml;

    /// <summary>
    /// Abstract parent class for request objects.<br/>
    /// <b>Author:</b> Kount <a>custserv@kount.com</a>;<br/>
    /// <b>Version:</b> 6.5.0. <br/>
    /// <b>Copyright:</b> 2010 Keynetics Inc <br/>
    /// </summary>
    public abstract class Request
    {
        /// <summary>
        /// Paypal type
        /// </summary>
        private const string PyplType = "PYPL";

        /// <summary>
        /// Google payment type
        /// </summary>
        private const string GoogType = "GOOG";

        /// <summary>
        /// Gift card payment type
        /// </summary>
        private const string GiftCardType = "GIFT";

        /// <summary>
        /// Credit card type
        /// </summary>
        private const string CardType = "CARD";

        /// <summary>
        /// The check type
        /// </summary>
        private const string ChekType = "CHEK";

        /// <summary>
        /// Bill me later type
        /// </summary>
        private const string BlmlType = "BLML";

        /// <summary>
        /// No payment type
        /// </summary>
        private const string NoneType = "NONE";

        /// <summary>
        /// Green Dot MoneyPak payment type
        /// </summary>
        private const string GdmpType = "GDMP";

        /// <summary>
        /// The RIS version
        /// </summary>
        private const string RisVersion = "0695";

        /// <summary>
        /// The Logger to use.
        /// </summary>
        private ILogger logger;

        /// <summary>
        /// Hash table of request data.
        /// </summary>
        private Hashtable data = null;

        /// <summary>
        /// URL of the target RIS server.
        /// </summary>
        private string url = null;

        /// <summary>
        /// RIS connect timeout in milliseconds.
        /// </summary>
        private int connectTimeout;

        /// <summary>
        /// Absolute path of the certificate file. Deprecated in favor of API key.
        /// </summary>
        private string certificate = null;

        /// <summary>
        /// Password used to export the certificate. Depcrecated in favor of API key.
        /// </summary>
        private string password = null;

        /// <summary>
        /// API key used for authentication to RIS server. Obtained from the AWC.
        /// </summary>
        private string apiKey = null;

        /// <summary>
        /// Construct a request object. Set the static setting from the
        /// web.config file.
        /// </summary>
        /// <param name="checkConfiguration">By default is true: will check config file if 
        /// `Ris.Url`, 
        /// `Ris.MerchantId`, 
        /// `Ris.Khash.Salt` are set.</param>
        /// <exception cref="Kount.Ris.RequestException">Thrown when there is
        /// static data missing for a RIS request.</exception>
        protected Request(bool checkConfiguration = true)
        {
            ILoggerFactory factory = LogFactory.GetLoggerFactory();
            this.logger = factory.GetLogger(typeof(Request).ToString());

            if (checkConfiguration)
            {
                this.CheckConfigurationParameter("Ris.MerchantId");
                this.CheckConfigurationParameter("Ris.Url");
                this.CheckConfigurationParameter("Ris.Khash.Salt");
            }
            
            // timeout must be always defined
            this.CheckConfigurationParameter("Ris.Connect.Timeout");

            this.data = new System.Collections.Hashtable();
            this.SetMerchantId(Int32.Parse(
                ConfigurationManager.AppSettings["Ris.MerchantId"]));

            Khash.Salt = ConfigurationManager.AppSettings["Ris.Khash.Salt"];

            var risVersion = String.IsNullOrEmpty(ConfigurationManager.AppSettings["Ris.Version"])
                        ? RisVersion
                        : ConfigurationManager.AppSettings["Ris.Version"];

            this.SetVersion(risVersion);
            this.SetUrl(ConfigurationManager.AppSettings["Ris.Url"]);
            this.connectTimeout = Int32.Parse(
                ConfigurationManager.AppSettings["Ris.Connect.Timeout"]);

            if (!String.IsNullOrEmpty(ConfigurationManager.AppSettings["Ris.API.Key"]))
            {
                this.SetApiKey(ConfigurationManager.AppSettings["Ris.API.Key"]);
            }
            else
            {
                this.CheckConfigurationParameter("Ris.CertificateFile");
                this.CheckConfigurationParameter("Ris.PrivateKeyPassword");
                this.SetCertificate(
                    ConfigurationManager.AppSettings["Ris.CertificateFile"],
                    ConfigurationManager.AppSettings["Ris.PrivateKeyPassword"]);
            }

            // KHASH payment encoding is set by default.
            this.SetKhashPaymentEncoding(true);
        }

        /// <summary>
        /// Gets hashtable data
        /// </summary>
        protected Hashtable Data
        {
            get { return this.data; }
        }

        /// <summary>
        /// Get the response from the RIS server.
        /// </summary>
        /// <param name="validate">default value is TRUE. If FALSE validate silently doesn't throw exception.</param>
        /// <returns>Kount.Ris.Response populated object.</returns>
        public Kount.Ris.Response GetResponse(bool validate = true)
        {
            this.logger.Debug("Kount.Ris.Request.GetResponse() - RIS " +
                "endpoint URL: " + this.url);
            this.logger.Debug($"PTOK [{this.SafeGet("PTOK")}]");
            string ptok = this.Data.ContainsKey("PTOK") ? (string)this.Data["PTOK"] : "";

            if (ptok.Equals("") && "KHASH".Equals((string)this.Data["PENC"]))
            {
                this.Data["PENC"] = "";
            }

            IList errors = this.Validate(this.data);
            if (errors.Count > 0)
            {
                string errorMsg = "";
                foreach (ValidationError error in errors)
                {
                    errorMsg += error.ToString() + "\n";
                }

                this.logger.Error("The following data validation errors occurred: " + errorMsg);
                if (validate)
                {
                    throw new Kount.Ris.ValidationException(errorMsg);
                }
            }

            string post = "";
            foreach (DictionaryEntry param in this.Data)
            {
                post = post + HttpUtility.UrlEncode(param.Key.ToString()) +
                    "=" + HttpUtility.UrlEncode(param.Value.ToString()) + "&";
                string value = param.Key.ToString().Equals("PTOK") ?
                    "payment token hidden" : param.Value.ToString();
                this.logger.Debug("[" + param.Key + "]=" + value);
            }

            post.TrimEnd('&');
            byte[] buffer = Encoding.ASCII.GetBytes(post);

            // Set up the request object
            HttpWebRequest webReq = (HttpWebRequest)WebRequest.Create(this.url);

            // Force using TLS 1.2 in case is not default - per request framework 4.5, 4, 3.5
            //System.Net.ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072 | (SecurityProtocolType)768;
            System.Net.ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;

            webReq.Timeout = this.connectTimeout;
            webReq.Method = "POST";
            webReq.ContentType = "application/x-www-form-urlencoded";
            webReq.ContentLength = buffer.Length;
            if (null != this.apiKey)
            {
                this.logger.Debug("setting API key header");
                webReq.Headers["X-Kount-Api-Key"] = this.apiKey;
            }
            else
            {
                this.logger.Debug("API key header not found, setting certificate");
                //// Add the RIS signed authentication certificate to the payload
                //// See Kount Technical Specifications Guide for details on
                //// requesting and exporting
                //// from your browser
                X509Certificate2 cert = new X509Certificate2();
                cert.Import(
                    this.GetCertificateFile(),
                    this.GetPrivateKeyPassword(),
                    X509KeyStorageFlags.MachineKeySet);
                X509CertificateCollection certs = webReq.ClientCertificates;
                certs.Add(cert);
                webReq.ClientCertificates.Add(cert);
            }

            // Call the RIS server and pass in the payload
            Stream postData = webReq.GetRequestStream();
            postData.Write(buffer, 0, buffer.Length);
            postData.Close();

            string risString;
            using (HttpWebResponse webResp = (HttpWebResponse)webReq.GetResponse())
            {
                // Read the RIS response string
                using (Stream answer = webResp.GetResponseStream())
                {
                    using (StreamReader risResponse = new StreamReader(answer))
                    {
                        risString = risResponse.ReadToEnd();
                    }
                }
            }

            this.logger.Debug("End GetResponse()");
            return new Kount.Ris.Response(risString);
        }

        /// <summary>
        /// Set parameters in the Response
        /// </summary>
        /// <param name="key">Parameter key</param>
        /// <param name="value">Parameter value</param>
        public void SetParameter(string key, string value)
        {
            this.Data[key] = value;
        }

        /// <summary>
        /// Set the mode of the transaction.
        /// </summary>
        /// <param name="mode">Depends on the request type.</param>
        public abstract void SetMode(char mode);

        /// <summary>
        /// Set the merchant Id.
        /// </summary>
        /// <param name="merchantId">Merchant Id.</param>
        public void SetMerchantId(int merchantId)
        {
            this.Data["MERC"] = merchantId;
        }

        /// <summary>
        /// Set the Kount Central customer Id.
        /// </summary>
        /// <param name="customerId">Kount Central customer Id.</param>
        public void SetKountCentralCustomerId(string customerId)
        {
            this.Data["CUSTOMER_ID"] = customerId;
        }

        /// <summary>
        /// Set the session ID of this session.
        /// </summary>
        /// <param name="sessionId">Session Id from the merchant.</param>
        public void SetSessionId(string sessionId)
        {
            this.Data["SESS"] = this.SafeGet(sessionId);
        }

        /// <summary>
        /// Set the merchant order number.
        /// </summary>
        /// <param name="orderNumber">Unique, up to 32 characters.</param>
        public void SetOrderNumber(string orderNumber)
        {
            this.Data["ORDR"] = this.SafeGet(orderNumber);
        }

        /// <summary>
        /// Set the merchant acknowledgement that this product will ship.
        /// </summary>
        /// <param name="mack">Set Y or N.</param>
        public void SetMack(char mack)
        {
            this.Data["MACK"] = mack;
        }

        /// <summary>
        /// Set the auth status of the payment.
        /// </summary>
        /// <param name="auth">Set A or D.</param>
        public void SetAuth(char auth)
        {
            this.Data["AUTH"] = auth;
        }

        /// <summary>
        /// Bankcard AVS ZIP CODE reply.
        /// </summary>
        /// <param name="avsz">M, N, or X.</param>
        public void SetAvsz(char avsz)
        {
            this.Data["AVSZ"] = avsz;
        }

        /// <summary>
        /// Bankcard AVS STREET ADDRESS reply.
        /// </summary>
        /// <param name="avst">M, N, or X.</param>
        public void SetAvst(char avst)
        {
            this.Data["AVST"] = avst;
        }

        /// <summary>
        /// Bankcard CVV/CVC/CVV2 reply.
        /// </summary>
        /// <param name="cvvr">M, N, or X.</param>
        public void SetCvvr(char cvvr)
        {
            this.Data["CVVR"] = cvvr;
        }

        /// <summary>
        /// Set a Paypal payment.
        /// </summary>
        /// <param name="paypalId">Paypal payer ID</param>
        public void SetPaypalPayment(string paypalId)
        {
            this.Data["PTYP"] = PyplType;
            this.SetPaymentToken(this.SafeGet(paypalId));
        }

        /// <summary>
        /// Set a google payment
        /// </summary>
        /// <param name="googleId">Google pay id</param>
        public void SetGooglePayment(string googleId)
        {
            this.Data["PTYP"] = GoogType;
            this.SetPaymentToken(this.SafeGet(googleId));
        }

        /// <summary>
        /// Set a credit card payment
        /// </summary>
        /// <param name="cardNumber">Raw credit card number</param>
        public void SetCardPayment(string cardNumber)
        {
            this.Data["PTYP"] = CardType;
            this.SetPaymentToken(this.SafeGet(cardNumber));
        }

        /// <summary>
        /// Sets a card payment and masks the card number in the following way: <br/>
	    /// First 6 characters remain as they are, following characters up to the last 4 are
	    /// replaced with the 'X' character, last 4 characters remain as they are.
	    /// If the provided Payment parameter is not a card payment, standard encoding
	    /// will be applied.
        /// </summary>
        /// <example> card number 0007380568572514 is masked to 000738XXXXXX2514 </example>
        /// <param name="cardNumber">Raw credit card number</param>
        public void SetCardPaymentMasked(string cardNumber)
        {
            this.Data["PTYP"] = CardType;
            this.Data["PENC"] = "MASK";

            string ptok = MaskToken(cardNumber);
            this.SetPaymentToken(this.SafeGet(ptok));
        }

        /// <summary>
        /// Set a check payment.
        /// </summary>
        /// <param name="micr">Micro number on the check.</param>
        public void SetCheckPayment(string micr)
        {
            this.Data["PTYP"] = ChekType;
            this.SetPaymentToken(this.SafeGet(micr));
        }

        /// <summary>
        /// Set a Bill Me Later payment.
        /// </summary>
        /// <param name="blmlId">bill me later id</param>
        public void SetBillMeLaterPayment(string blmlId)
        {
            this.Data["PTYP"] = BlmlType;
            this.SetPaymentToken(this.SafeGet(blmlId));
        }

        /// <summary>
        /// Set No Payment.
        /// </summary>
        public void SetNoPayment()
        {
            this.Data["PTYP"] = NoneType;
            this.Data["PTOK"] = "";
        }

        /// <summary>
        /// Set a gift card payment
        /// </summary>
        /// <param name="giftCardNum">Gift card number</param>
        public void SetGiftCardPayment(string giftCardNum)
        {
            this.Data["PTYP"] = GiftCardType;
            this.SetPaymentToken(this.SafeGet(giftCardNum));
        }

        /// <summary>
        /// Set a payment type and payment token
        /// </summary>
        /// <param name="ptyp">Payment Type</param>
        /// <param name="ptok">Payment Token</param>
        public void SetPayment(string ptyp, string ptok)
        {
            this.logger.Debug("Kount.Ris.Request.SetPayment()");
            this.Data["PTYP"] = ptyp;
            this.SetPaymentToken(this.SafeGet(ptok));
        }

        /// <summary>
        /// Set a Green Dot MoneyPak payment.
        /// </summary>
        /// <param name="id">Green Dot MoneyPak payment ID number</param>
        public void SetGreenDotMoneyPakPayment(string id)
        {
            this.Data["PTYP"] = GdmpType;
            this.SetPaymentToken(this.SafeGet(id));
        }

        /// <summary>
        /// Get value from Data - Hashtable.
        /// </summary>
        /// <param name="param">Key string in hashtable</param>
        /// <returns></returns>
        public string GetParam(string param)
        {
            if (String.IsNullOrEmpty(param))
            {
                return String.Empty;
            }

            string res = this.Data[param] as string;
            return res ?? String.Empty;
        }

        /// <summary>
        /// Get the URL of the target RIS server.
        /// </summary>
        /// <returns>String of the target url.</returns>
        public string GetUrl()
        {
            return this.url;
        }

        /// <summary>
        /// Set the URL of the target RIS server.
        /// </summary>
        /// <param name="url">String of the target RIS server.</param>
        public void SetUrl(string url)
        {
            this.url = url;
        }

        /// <summary>
        /// Set the RIS certificate information.
        /// </summary>
        /// <param name="certificate">Full path of the certificate pk12 or
        /// pfx file.</param>
        /// <param name="password">Password used to export the certificate.
        /// </param>
        public void SetCertificate(string certificate, string password)
        {
            this.certificate = certificate;
            this.password = password;
        }

        /// <summary>
        /// Set the API key.
        /// </summary>
        /// <param name="key">Key used to authenticate.</param>
        public void SetApiKey(string key)
        {
            this.apiKey = key;
        }

        /// <summary>
        /// Get the certificate file path.
        /// </summary>
        /// <returns>String of the certificate file path.</returns>
        public string GetCertificateFile()
        {
            return this.certificate;
        }

        /// <summary>
        /// Private key password used to export the certificate file.
        /// </summary>
        /// <returns>String of the certificate export password.</returns>
        public string GetPrivateKeyPassword()
        {
            return this.password;
        }

        /// <summary>
        /// Set the RIS payment encoding to KHASH.
        /// </summary>
        [Obsolete("Version 5.0.0. Use Kount.Ris.Request.SetKhashPaymentEncoding(bool) : void")]
        public void SetKhashPaymentEncoding()
        {
            string message = "The method " +
                "Kount.Ris.Request.SetKhashPaymentEncoding() is obsolete. " +
                "Use Kount.Ris.Request.SetKhashPaymentEncoding(bool) instead.";
            this.logger.Info(message);
            this.Data["PENC"] = "KHASH";
        }

        /// <summary>
        /// Set the RIS payment encoding to KHASH.
        /// </summary>
        /// <param name="enabled">TRUE when enabled</param>
        public void SetKhashPaymentEncoding(bool enabled)
        {
            if (enabled)
            {
                this.Data["PENC"] = "KHASH";
            }
            else
            {
                this.Data["PENC"] = "";
            }
        }

        /// <summary>
        /// Set the last 4 characters of the payment token.
        /// </summary>
        /// <param name="last4">Last 4 characters</param>
        public void SetPaymentTokenLast4(string last4)
        {
            this.Data["LAST4"] = last4;
        }

        /// <summary>
        /// Set the version of the RIS response.
        /// </summary>
        /// <param name="version">Response version.</param>
        public void SetVersion(string version)
        {
            this.Data["VERS"] = version;
        }

        /// <summary>
        /// Check if KHASH payment encoding has been set.
        /// </summary>
        /// <returns>TRUE when set</returns>
        protected bool IsSetKhashPaymentEncoding()
        {
            return this.Data.ContainsKey("PENC") &&
                "KHASH".Equals(this.Data["PENC"]);
        }

        /// <summary>
        /// Set the payment token.
        /// </summary>
        /// <param name="token">Payment token</param>
        protected void SetPaymentToken(string token)
        {
            string raw = token;
            if (null != token && !this.Data.Contains("LAST4"))
            {
                if (token.Length > 4)
                {
                    this.Data["LAST4"] = token.Substring(token.Length - 4);
                }
                else
                {
                    this.Data["LAST4"] = token;
                }
            }

            if (this.IsSetKhashPaymentEncoding())
            {
                token = (GiftCardType.Equals(this.Data["PTYP"])) ?
                    Khash.HashGiftCard((int)this.Data["MERC"], token) :
                    Khash.HashPaymentToken(token);
            }

            this.Data["PTOK"] = token;
        }

        /// <summary>
        /// Check configuration parameters for existence in application
        /// configuration.
        /// </summary>
        /// <param name="parameter">Parameter name</param>
        /// <exception cref="Kount.Ris.RequestException">Thrown when parameter
        /// is missing</exception>
        protected void CheckConfigurationParameter(string parameter)
        {
            if (null == ConfigurationManager.AppSettings[parameter])
            {
                this.logger.Error($"Configuration parameter [{parameter}] not defined.");
                throw new Kount.Ris.RequestException(
                    $"[{parameter}] must be defined in the application configuration file.");
            }
        }

        /// <summary>
        /// Sanitize a variable before return it.
        /// </summary>
        /// <param name="var">Raw variable</param>
        /// <returns>Sanitized variable</returns>
        protected string SafeGet(string var)
        {
            return (null == var) ? "" : var;
        }

        /// <summary>
        /// Validate the RIS request.
        /// </summary>
        /// <param name="data">Throws an exception if validation fails.</param>
        /// <returns>List of invalid elements</returns>
        private IList Validate(Hashtable data)
        {
            IList errors = new ArrayList();
            this.logger.Debug("start validate()");
            var doc = new XmlDocument();

            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("KountRisSdk.validate.xml"))
            {
                using (StreamReader reader = new StreamReader(s))
                {
                    doc.LoadXml(reader.ReadToEnd());
                }
            }

            XmlNodeList nodes = doc.GetElementsByTagName("param");
            XmlNodeList modes = null;
            XmlNode required = null;
            string name = null;

            Hashtable arrayParams = this.FetchArrayParams(data);

            foreach (XmlNode node in nodes)
            {
                name = node.Attributes["name"].Value;
                required = node.SelectSingleNode("required");

                // check required field
                if (null != required)
                {
                    modes = required.SelectNodes("mode");
                    var mode = (char)this.Data["MODE"];

                    //// check for specific modes
                    if (!data.Contains(name)
                        && !arrayParams.Contains(name)
                        && ((0 == modes.Count)
                          || ((0 < modes.Count) && (null != required.SelectSingleNode($"mode[. ='{mode}']")))))
                    {
                        this.logger.Error($"Validate XML loop. Missing required field [{name}], Mode [{mode}]");
                        errors.Add(new ValidationError(name, mode.ToString()));
                    }
                }

                if (data.Contains(name))
                {
                    this.ValidateHelper(node, name, errors);
                }
                else if (arrayParams.Contains(name))
                {
                    var keys = (ArrayList)arrayParams[name];
                    foreach (string key in keys)
                    {
                        this.ValidateHelper(node, key, errors);
                    }
                }
            }

            return errors;
        }

        /// <summary>
        /// Encodes the provided payment token according to the MASK encoding scheme
        /// </summary>
        /// <param name="token">CARD token</param>
        /// <returns>masked token</returns>
        private static string MaskToken(string token)
        {
            var builder = new StringBuilder();

            builder.Append(token.Substring(0, 6));
            for (int i = 6; i < (token.Length - 4); i++)
            {
                builder.Append('X');
            }

            builder.Append(token.Substring(token.Length - 4));

            return builder.ToString();
        }

        /// <summary>
        /// Validation helper method
        /// </summary>
        /// <param name="node">XML node containing the validation data</param>
        /// <param name="name">The value to validate</param>
        /// <param name="errors">The list of validation errors</param>
        private void ValidateHelper(XmlNode node, string name, IList errors)
        {
            XmlNode maxLength = null;
            XmlNode regex = null;
            string dataValue = (this.Data[name] != null) ? this.Data[name].ToString() : null;

            // check max length
            if (dataValue != null
                && this.data.Contains(name)
                && ((null != (maxLength = node.SelectSingleNode("max_length")))
                && (Int32.Parse(maxLength.InnerText) < dataValue.Length)))
            {
                this.logger.Error($"Validate error: Field [{name}] is too long. Value [{this.Data[name]}]");
                var length = Int32.Parse(maxLength.InnerText);
                errors.Add(new ValidationError(name, dataValue, length));
            }

            regex = node.SelectSingleNode("reg_ex");

            // check regular expression
            if (this.data.Contains(name) && (null != regex))
            {
                var regValidator = new RegexStringValidator(regex.InnerText);
                try
                {
                    regValidator.Validate(dataValue);
                }
                catch (System.ArgumentException sysarg)
                {
                    this.logger.Error($"Regexp validation failed. Field [{name}], Value [{this.Data[name]}], Pattern [{regex.InnerText}]", sysarg);
                    errors.Add(new ValidationError(name, dataValue, regex.InnerText));
                }
            }
        }

        /// <summary>
        /// Fetch data parameters in arrays
        /// </summary>
        /// <param name="data">The data hashtable</param>
        /// <returns>A hashtable of array data</returns>
        private Hashtable FetchArrayParams(Hashtable data)
        {
            Hashtable arrayParams = new Hashtable();
            ArrayList prod_type = new ArrayList();
            ArrayList prod_item = new ArrayList();
            ArrayList prod_desc = new ArrayList();
            ArrayList prod_quant = new ArrayList();
            ArrayList prod_price = new ArrayList();

            foreach (string key in data.Keys)
            {
                if (key.StartsWith("PROD_TYPE"))
                {
                    prod_type.Add(key);
                }
                else if (key.StartsWith("PROD_ITEM"))
                {
                    prod_item.Add(key);
                }
                else if (key.StartsWith("PROD_DESC"))
                {
                    prod_desc.Add(key);
                }
                else if (key.StartsWith("PROD_QUANT"))
                {
                    prod_quant.Add(key);
                }
                else if (key.StartsWith("PROD_PRICE"))
                {
                    prod_price.Add(key);
                }
            }

            arrayParams.Add("PROD_TYPE", prod_type);
            arrayParams.Add("PROD_ITEM", prod_item);
            arrayParams.Add("PROD_DESC", prod_desc);
            arrayParams.Add("PROD_QUANT", prod_quant);
            arrayParams.Add("PROD_PRICE", prod_price);

            return arrayParams;
        }
    }
}