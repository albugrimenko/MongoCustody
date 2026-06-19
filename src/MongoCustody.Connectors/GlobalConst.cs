namespace MongoCustody.Connectors
{
    /// <summary>
    /// Global constants
    /// </summary>
    internal static class GlobalConst
    {

        /// <summary>
        /// Key name: MongoDB connection string
        /// MONGOCUSTODY_MONGO_CONNECTION_STRING
        /// </summary>
        internal static string KEYNAME_MONGODB_CONNSTRING = "APPSETTING_COSMOS_CONNSTRING";

        /// <summary>
        /// Key name: Azure File Storage connection string
        /// mongocustody-storage-connection-string
        /// </summary>
        internal static string KEYNAME_AZBLOB_CONNSTRING = "APPSETTING_AZBLOB_CONNSTRING";
    }
}
