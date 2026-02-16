using System;
using Npgsql;

class Program
{
    static void Main()
    {
        string connectionString = "Host=localhost;Port=5432;Username=postgres;Password=094825;Database=gov_contracts_dw";

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();

            Console.WriteLine("Connected to PostgreSQL successfully!\n");

            string sql = "SELECT agency_name FROM agency;";

            using var command = new NpgsqlCommand(sql, connection);
            using var reader = command.ExecuteReader();

            Console.WriteLine("Agencies in Database:\n");

            while (reader.Read())
            {
                Console.WriteLine($"- {reader.GetString(0)}");
            }

            connection.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error connecting to database:");
            Console.WriteLine(ex.Message);
        }
    }
}
